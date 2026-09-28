import fs from 'fs';
import { chooseRoute, shopDetour } from './route.mjs';
// A bot that plays a run through the DevTools agent API (Debug build of DevTools, game running,
// hero in a run - /flow/start_solo gets one). It plays as a player could: only the honest routes,
// no /cheat or /console, and /reflect only to read (get, find, and calls of getters such as a cost
// or a localized name - never a set). Per room: fight, loot (shrines, merchants, deposits, wells),
// then the world map toward the boss; through the boss's rift into the next zone, on through Despair
// and the side rift to Primus, the final boss; stops at the ending (a victory) or when the hero dies.
// Runs with DevTools loaded earn no progression.
//
//   node tools/devbot.mjs auto [maxRooms] [fightsSoFar]   the whole run; fightsSoFar when resuming
//   node tools/devbot.mjs fight | loot | travel            one step, in the room the hero is in
//
// Environment: DEVTOOLS_PORT (47653), DEVBOT_TRACE=<file> for a per-tick log of boss fights,
// DEVBOT_EVENTS=<file> for one JSON line per milestone (zone and room entered, boss down, death,
// end) - what metrics.mjs measures a run by.
// Needs Node 18+ (fetch).
const B = 'http://127.0.0.1:' + (process.env.DEVTOOLS_PORT || 47653);
const TRACE = process.env.DEVBOT_TRACE;
const trace = line => { if (TRACE) fs.appendFileSync(TRACE, line + '\n'); };
const EVENTS = process.env.DEVBOT_EVENTS;
const emit = (ev, data = {}) => { if (EVENTS) fs.appendFileSync(EVENTS, JSON.stringify({ t: Date.now(), ev, ...data }) + '\n'); };
const sleep = ms => new Promise(r => setTimeout(r, ms));
const log = (...a) => console.log(new Date().toISOString().slice(11, 19), ...a);

async function call(method, path, body) {
  const opts = { method, headers: {} };
  if (method === 'POST') { opts.headers['Content-Type'] = 'application/json'; opts.body = JSON.stringify(body || {}); }
  const res = await fetch(B + path, opts);
  const json = await res.json();
  if (!json.ok) { const e = new Error(json.error); e.api = true; throw e; }
  return json.result;
}
// Reads are retried a few times: between rooms the game can answer with an error for a frame.
const get = async (p, q) => {
  for (let i = 0; ; i++) {
    try { return await call('GET', p + (q ? '?' + new URLSearchParams(q) : '')); }
    catch (e) { if (i >= 3 || /no hero|no local|not in a run/i.test(e.message)) throw e; await new Promise(r => setTimeout(r, 300)); }
  }
};
const post = (p, b) => call('POST', p, b);
// dashAt: when the last dash was sent (walkSpeed does not read the dash and the landing as walking - iteration 19).
let dashAt = 0;
const tryPost = async (p, b) => { if (p === '/hero/cast' && b && b.slot === 'Movement') dashAt = Date.now(); try { return await post(p, b); } catch (e) { return { error: e.message }; } };

const dist = (a, b) => Math.hypot(a.x - b.x, a.z - b.z);

async function handleBlocking(st) {
  if (st.message) {
    const pref = ['Yes', 'Ok', 'Custom0', 'No', 'Cancel'];
    const b = pref.find(p => st.message.buttons.some(x => x.button === p));
    log('message:', st.message.text.slice(0, 80), '->', b);
    await tryPost('/message/answer', { button: b });
    return true;
  }
  if (st.conversation) {
    if (st.conversation.choices) { log('conversation choice:', st.conversation.choices.join(' / ')); await tryPost('/conversation/choose', { index: 0 }); }
    else await tryPost('/conversation/advance');
    return true;
  }
  return false;
}

// ----- fighting -----------------------------------------------------------------------------
// Positioning by going around them, not away from them. The hero circles the fight - the boss,
// or the middle of the pack near it - at shooting range, sideways to it, the way a player strafes:
// sideways is what makes shots miss, and a circle never ends at a wall the way backing off does.
// When the way round runs into a wall or into more of them, it turns and goes round the other way.
//
// Cells come from /nav/grid (walkable, path length, room to move). Each candidate step 2-5.5 m
// away is scored for: going sideways around the centre, holding the range, room around it, not
// being next to an enemy, not walking through them, and not standing on a line a projectile
// (/threats) is flying along. A dash takes the same measure, minus the walking-through-them part:
// dashing past them into open ground is how a player gets out of a corner.
const isBossE = e => e.monsterType === 'Boss' || e.monsterType === 'MiniBoss';

// A shielded boss (iteration 13). Infernus (LavaLand) at ~55% takes Se_Mon_LavaLand_BossInfernus_InvulShield
// (history/it7, history/it13, decompiled): invulnerable and unstoppable, and it spawns
// Mon_LavaLand_InfernusPillar monsters - clamp(2 + live heroes, 3, 5), one fewer on an easy difficulty -
// at LavaLand_InfernusPillarPosition's points; the shield ends only when every pillar is destroyed
// (each pillar's OnDestroyed counts down). The pillars shoot Ai_Mon_LavaLand_InfernusPillar_Projectile
// at Infernus, not at the hero (never once in "took"). run-006 (6783) and run-017 (7253, 60 s) kept
// shooting the frozen Infernus with the pillars "near" (the log's 1 near -> 3 near) and died to its
// swings. So a target that is invulnerable is not shot while something else can be: the kinds that must
// die (MUST_KILL) first, then the nearest. /entities says `invulnerable` (status.hasInvulnerable).
// With nothing else to shoot, the skills are held for when it ends and the hero keeps farther off.
// Iteration 35: Azurak's roll pillars too (Ai_Mon_Despair_BossAzurak_Roll: he rolls to and fro across the arena until every
// one of them is down - by his own roll into them, or by us; then he burrows and comes up at the centre). The pillars of his
// Roar are the same monster but ours (spawned for the nearest hero's player - not in the enemies' list): their shadows are
// where the roar does not reach (roarSafe).
const MUST_KILL = /InfernusPillar|AzurakRollPillar/;
// Strike rains where the walk should keep its way (plan()'s momentum): Infernus's meteors (iteration 17) and Nyx's
// Starfall (iteration 18: run-026's walk flipped east/west every look between strikes, 0.1 s from landing).
const RAIN = /BossInfernus_Meteor|Starfall/;
// Iteration 35: the weapon Primus drops at a phase change flies off as a projectile with nothing to deal (PhaseSwitcher_GreatSword/DualSword).
const HARMLESS_SHOT = /InfernusPillar_Projectile|BossPrimusAeron_PhaseSwitcher_/;
// Infernus's Atk (an InstantDamageInstance with no delay - never listed as a strike) landed at 6.3-7.2 m
// of its centre in run-017 (x5, 78 each, each relighting the burn), right where plan()'s shooting range
// (0.85 x 8.65 m) keeps the hero. Shooting something else, the hero keeps BOSS_KEEP from it.
const BOSS_KEEP = 8.5, INVUL_RANGE = 11;
// Iteration 21 (run-031's death): a shielded or invulnerable zone boss is kept off harder (KEEP_W in plan()) and, within
// SHIELD_REACH of the hero, dashed away from (keepOffDash) - Infernus's Atk (no telegraph, no /threats area) hit at 6.8 m
// (run-031 x2), 6.9 and 7.5 m (run-023) for 78 + its burn.
const KEEP_W = 3.5, SHIELD_REACH = 7.8, SHIELD_AWAY = 9.5, WALKUP_KEEP = 10;
// Iteration 28: how far along the straight walk-up (and the quiet walks) timed red is looked for (redInWay), and how soon it must
// land to be waited out rather than ignored (RED_WAIT_LEFT s; a walk to the loot or the exit stands at most RED_WAIT_MAX ms).
const WALKUP_LOOK = 10, RED_WAIT_LEFT = 2.5, RED_WAIT_MAX = 3000;
// The one a fight would shoot: the zone boss, else a miniboss, else the nearest.
const firstTarget = entities => entities.find(e => e.monsterType === 'Boss') || entities.filter(isBossE)[0] || entities[0];
// A pillar in sight counts as the shield up even if the boss's `invulnerable` did not say so.
const shieldSign = entities => entities.some(e => MUST_KILL.test(e.type || ''));
// Iteration 16: only at a zone boss (or with a pillar in sight). run-022 logged "boss shielded / boss
// invulnerable" ~60 times for ordinary monsters and minibosses in their 0.1-1.1 s of spawn (or burrow)
// invulnerability - Cave Spider, Night Olm, Soul Swordsman, Thunder Tiger - and "nothing else to shoot"
// held the skills and backed the hero off to 11 m from them. Without a zone boss: the nearest one that can
// be hurt, as a plain target (no keep-off, no log); all invulnerable - the first (the casts wait for it).
// Iteration 26: `cannot(e)` says an entity cannot be hurt now (fight()'s blockOf: the flag, the game's own immunity check,
// the frozen-hp fallback); by default the `invulnerable` flag alone, as before.
function chooseTarget(me, entities, far = [], cannot = e => !!e.invulnerable) {
  const first = firstTarget(entities);
  if (!first) return { target: first, why: null };
  const must = e => MUST_KILL.test(e.type || '') ? 1 : 0;
  if (first.monsterType !== 'Boss' && !shieldSign(entities)) {
    if (!cannot(first)) return { target: first, why: null };
    const open = entities.filter(e => e && !cannot(e) && e.alive !== false).sort((x, y) => dist(me, x.position) - dist(me, y.position));
    return { target: open[0] || first, why: null };
  }
  const seen = new Set([first.id]);
  const others = entities.concat(far).filter(e => {
    if (!e || cannot(e) || e.alive === false || seen.has(e.id)) return false;
    seen.add(e.id);
    return must(e) || e.monsterType !== 'Boss';
  });
  const pillars = others.filter(must);
  if (!cannot(first) && !pillars.length) return { target: first, why: null };
  others.sort((x, y) => must(y) - must(x) || dist(me, x.position) - dist(me, y.position));
  if (!others.length) return { target: first, why: 'invulnerable' };
  return { target: others[0], why: 'shielded', shielded: first, must: pillars.length };
}

// ----- a boss that cannot be hurt now (iteration 26) ---------------------------------------------------------------------
// The user: "the bot likes to hammer the boss during its invulnerability phase - it runs and dodges, but keeps trying to
// damage the boss". Iteration 13 held the skills at an `invulnerable` target; the basic attack (/hero/attack_in_place every
// look) was never held. Runs 020-039 traced 33 phases with the flag and the attack went on through each: Dark Moon's
// Eclipse (25.5-26.5 s, at 65%) and her phase change (8.9-9.1 s, at 35%), White Night's Cataclysm (26.5 s), Skoll's Death
// From Above (10.8-11.0 s), Nyx's phase changes (4.0-4.1 s), Infernus's shield (13-29 s), the Seeker's vanish. Decompiled
// (history/it26): the game's own damage check (Actor, DealDamage) is Status.hasDamageImmunity = Invulnerable or Protected
// (/entities says only the first); Skoll's rise, the Seeker's tunnel vanish and Infernus's jump are Untargetable as well
// (the Seeker's Blink is Untargetable alone, ~1 s). Nyx's phase change ends in an explosion round her and a shield of 20%
// of her max health that decays over 6 s (GiveShield(.., 6, isDecay)): her hp stands still while it soaks the hits.
// No phase in those runs froze a boss's hp in reach of the attack without the flag (the unflagged stretches were the hero
// out of reach - Nyx's Blackhole - or rooted); the frozen-hp rule below is the fallback for what is not known.
//
// What stops a hit on e now, or null: the flag; the mod's `immune` / `untargetable` (proposals/iter-26-mod.md) or the same
// read by reflection for the zone boss (ex: { immune, untargetable }); the frozen-hp fallback (frozen).
function hurtBlock(e, ex, frozen) {
  if (!e) return null;
  if (e.invulnerable) return 'invulnerable';
  if (e.immune === true || (ex && ex.immune === true)) return 'immune';
  if (e.untargetable === true || (ex && ex.untargetable === true)) return 'untargetable';
  return frozen ? 'frozen' : null;
}
// The fallback: a boss or miniboss whose hp + shield has not fallen at all over FROZEN_T s of looks in which the hero's
// basic attack went at it from within reach counts as unhurtable for FROZEN_HOLD s (no attacks, no skills), then the
// attack goes at it again to see. Any fall of hp + shield ends it at once; a rise (a heal, Dark Moon's SetHealth back to
// her threshold) is the new mark. fz is kept by fight(); s: { id, sample: hp + shield, hitting, now }. True while frozen.
const FROZEN_T = 1.6, FROZEN_HOLD = 2.5;
function frozenStep(fz, s) {
  if (fz.id !== s.id) { Object.assign(fz, { id: s.id, sample: s.sample, hitMs: 0, lastT: s.now, until: 0, n: 0 }); return false; }
  const dt = Math.max(0, Math.min(500, s.now - fz.lastT));
  fz.lastT = s.now;
  if (s.sample < fz.sample - 0.5) { fz.sample = s.sample; fz.hitMs = 0; fz.until = 0; return false; }
  if (s.sample > fz.sample) fz.sample = s.sample;
  if (fz.until > s.now) return true;
  if (fz.until) { fz.until = 0; fz.hitMs = 0; }
  if (s.hitting) fz.hitMs += dt;
  if (fz.hitMs >= FROZEN_T * 1000) { fz.until = s.now + FROZEN_HOLD * 1000; fz.n++; return true; }
  return false;
}
// The phases, by the status effect that makes them (the mod's `effects`, or Status.statusEffects read by reflection), else
// by the boss's type and health: how long each held the flag in runs 020-039, whether waiting out its end within reach is
// safe (pre: Dark Moon lands 5 m in front of where she hung and stays dazed; the Ink phase change's knockback is no blow),
// and whether the boss can swing meanwhile (idle: channelled, dazed or in the air - no 'boss close' dash on it).
// Not pre: Nyx's ends in an explosion round her, Skoll's in a landing that follows the hero, the Cataclysm in its waves.
const BOSS_PHASES = [
  { fx: /BossDarkMoon_Eclipse/, boss: /BossDarkMoon/, above: 0.5, name: "Dark Moon's Eclipse", len: 25.5, pre: true, idle: true },
  { fx: /Ink_Boss_PhaseChange/, boss: /BossDarkMoon/, name: "Dark Moon's phase change", len: 8.9, pre: true, idle: true },
  { fx: /BossWhiteNight_Cataclysm/, boss: /BossWhiteNight/, above: 0.5, name: "White Night's Cataclysm", len: 26.5, pre: false, idle: true },
  { fx: /Ink_Boss_PhaseChange/, boss: /BossWhiteNight/, name: "White Night's phase change", len: 9.0, pre: true, idle: true },
  { fx: /BossSkoll_DeathFromAbove/, boss: /BossSkoll/, name: "Skoll's Death From Above", len: 10.8, pre: false, idle: true },
  { fx: /BossNyx_PhaseChange/, boss: /BossNyx/, name: "Nyx's phase change", len: 4.0, pre: false, idle: true },
  { fx: /BossInfernus_InvulShield/, boss: /BossInfernus/, name: "Infernus's shield", len: null, pre: false, idle: false },
  { fx: /BossInfernus_Jump/, boss: /BossInfernus/, name: "Infernus's jump", len: null, pre: false, idle: false },
  { fx: /BossSeeker/, boss: /BossSeeker/, name: "the Seeker's vanish", len: null, pre: false, idle: false },
  // Iteration 35 (decompiled, not seen yet). Azurak's burrow after his roll (Se_Mon_Despair_BossAzurak_Hide): he sinks
  // (burrowTime + 0.1), turns invulnerable, is moved to the arena's centre and comes up there over unburrowTime (~1 s) - no
  // blow as he comes up, so closing in on the centre is safe.
  { fx: /BossAzurak_Hide/, boss: /BossAzurak/, name: "Azurak's burrow", len: 1.1, pre: true, idle: true },
  // Primus (history/it35/dec): its Doom (Adapt < 50%: invulnerable at the arena's centre while meteors fall, ~18 s) and its
  // Gold Rain (Force < 35%: gone between slashes) by their effects; anything else invulnerable is its phase change
  // (InTransition: an InvulnerableEffect with no name of its own - the blast, floor pieces broken, then postDaze; Adapt comes
  // back healed to full). `above: 1.01` keeps the named ones out of the by-type guess.
  { fx: /BossPrimusAeron_Adapt_Doom/, boss: /BossPrimus/, above: 1.01, name: "Primus's Doom", len: null, pre: false, idle: true },
  { fx: /BossPrimusAeron_Force_GoldRain/, boss: /BossPrimus/, above: 1.01, name: "Primus's Gold Rain", len: null, pre: false, idle: false },
  { fx: /(?!)/, boss: /BossPrimus/, name: "Primus's phase change", len: null, pre: false, idle: true },
];
function bossPhase(boss, effects) {
  if (!boss) return null;
  const types = (effects || []).map(x => typeof x === 'string' ? x : x && (x.type || x.$type)).filter(Boolean);
  const own = BOSS_PHASES.filter(p => p.boss.test(boss.type || ''));
  const byFx = own.find(p => types.some(t => p.fx.test(t)));
  if (byFx) return { ...byFx, by: types.find(t => byFx.fx.test(t)) };
  const frac = boss.maxHp ? boss.hp / boss.maxHp : 1;
  const byType = own.find(p => p.above == null || frac > p.above);
  return byType ? { ...byType, by: 'type' } : { name: `${boss.name || boss.type}'s phase`, len: null, pre: false, idle: false, by: 'unknown' };
}
// Where to wait while a zone boss cannot be hurt: INVUL_RANGE off (fight()); from PRE_T s before the known end of a phase
// whose end is safe, PRE_RANGE from it (plan()'s boss range is 0.85 x 8.65 = 7.35 m) - in reach of the basic attack and of
// a full Precision Shot (~14.6 m) the moment it ends, with the skills' cooldowns full (held all along).
const PRE_T = 2.5, PRE_RANGE = 7.5, PRE_KEEP = 6;
function phaseHold(phase, since, now) {
  const el = (now - since) / 1000;
  const left = phase && phase.len ? Math.max(0, phase.len - el) : null;
  return { el, left, pre: !!(phase && phase.pre && left != null && left <= PRE_T) };
}
// Nyx's decaying shield (20% of her max over 6 s after her phase change; any big shield on a zone boss): Precision Shot
// (the burst, ~10 s cooldown) waits until it is under SHIELD_HOLD of max health - the basic attack goes on (what it takes
// off the shield brings the hp damage forward: the shield would run out by itself at 6 s).
const SHIELD_HOLD = 0.05;
// The basic attack's target: a deposit in reach (it breaks), else the fight's target unless it cannot be hurt now - null: none.
const attackPick = (target, deposit, cannot) => deposit || (target && !cannot(target) ? target : null);
// Iteration 35: not Primus's Rage shield (the missing health as a shield that decays over 90 s - the hits on it are the fight).
const bigShield = e => !!(e && e.monsterType === 'Boss' && e.maxHp > 0 && (e.shield || 0) >= SHIELD_HOLD * e.maxHp && !/BossPrimus/.test(e.type || ''));

let damageSeq = -1;   // /damage: hits seen so far (-1: none read yet, skip the backlog)
let orbitSide = 1;   // +1 counter-clockwise, -1 clockwise around the centre
let orbitFlippedAt = 0;

function segDist(p, a, b) {
  const vx = b.x - a.x, vz = b.z - a.z;
  const l2 = vx * vx + vz * vz || 1e-6;
  const t = Math.max(0, Math.min(1, ((p.x - a.x) * vx + (p.z - a.z) * vz) / l2));
  return Math.hypot(a.x + vx * t - p.x, a.z + vz * t - p.z);
}

// How deep a point is inside a telegraph (with a margin for the hero's body); 0 outside.
function areaDepth(p, a, margin = 0.7) {
  // Iteration 35: a polygon (/threats `poly`: a floor piece of Primus's arena about to break) - inside: the way to its
  // nearest edge; outside within the margin: what is left of the margin.
  if (a.shape === 'poly' && Array.isArray(a.corners) && a.corners.length >= 3) {
    const d = polyDepth(p, a.corners) + margin;   // (iteration 20's: the way to the nearest edge, negative outside)
    return d > 0 ? d : 0;
  }
  if (a.shape === 'box') {
    const c = a.corners;
    const ux = c[1].x - c[0].x, uz = c[1].z - c[0].z, vx = c[0].x - c[3].x, vz = c[0].z - c[3].z;
    const lu = Math.hypot(ux, uz) || 1, lv = Math.hypot(vx, vz) || 1;
    const dx = p.x - a.centre.x, dz = p.z - a.centre.z;
    const pu = Math.abs((dx * ux + dz * uz) / lu), pv = Math.abs((dx * vx + dz * vz) / lv);
    const d = Math.min(lu / 2 + margin - pu, lv / 2 + margin - pv);
    return d > 0 ? d : 0;
  }
  const dx = p.x - a.centre.x, dz = p.z - a.centre.z;
  const d = Math.hypot(dx, dz);
  let depth = a.radius + margin - d;
  if (a.inner > 0.05) depth = Math.min(depth, d - (a.inner - margin));
  if (depth <= 0) return 0;
  if (a.shape === 'slice' && d > 0.5) {
    const cos = (dx * a.facing.x + dz * a.facing.z) / d;
    const ang = Math.acos(Math.max(-1, Math.min(1, cos))) * 180 / Math.PI;
    if (ang > a.angle / 2 + 12) return 0;
  }
  return depth;
}

// The red on the ground as fight() uses it. /threats lists telegraphs (circle, ring, slice, box -
// each with its fill and seconds left), blobs (a drawing with no timer: fill -1, read here as half
// full and landing in 1 s, `timeless`), and strikes: blows that land at a point after a delay
// (InstantDamageInstance - Skoll's swords, the Seeker's DelayedExplosion, Starfall), read from the
// damage itself - its real radius, fill and seconds left, `type` naming it; "strikebox" is a box
// read as a circle of its half-length. A strike is a circle to areaDepth. The blob drawn for the
// same blow is only its renderer bounds (8-11 m for Skoll's swords, which hit from much closer -
// run-003's death), so a blob whose centre is within 1.5 m of a strike is dropped, and within 1.5 m
// of one that went off in the last 0.6 s too (the drawing outlives the blow by ~0.1-0.2 s).
//   strikesSeen  Map kept by the caller across ticks: where strikes were, and when last listed.
function readAreas(raw, strikesSeen, nowMs) {
  for (const a of raw) if (a.shape === 'strike' || a.shape === 'strikebox')
    strikesSeen.set(Math.round(a.centre.x * 2) + ':' + Math.round(a.centre.z * 2), { centre: a.centre, last: nowMs });
  for (const [k, s] of strikesSeen) if (nowMs - s.last > 600) strikesSeen.delete(k);
  const drawing = a => { for (const s of strikesSeen.values()) if (dist(a.centre, s.centre) < 1.5) return true; return false; };
  return raw.filter(a => a.shape !== 'blob' || !drawing(a))
    .map(a => a.fill < 0 ? { ...a, fill: 0.5, left: 1, timeless: true } : a);
}
const isStrike = a => a.shape === 'strike' || a.shape === 'strikebox';
// A strike's reach (iteration 18): the blow hits a hero whose body touches its collider, so it reaches past the
// radius /threats gives (the collider's own). Nyx's Starfall (2.35 m) hit at 2.30, 2.32, 2.45 and 2.65 m from its
// centre (probe.jsonl hits, run-023..026) - every one at the edge, the last 0.3 m outside it (run-026: the dash
// came at 0.1 m in with 0.03 s left). So every strike is widened by STRIKE_REACH for all that reads the red
// (plan's cells, the escape and its urgency, crossRed, the Blackhole's dodge); `reach` marks it.
const STRIKE_REACH = 0.35;
function addReach(areas) {
  for (let i = 0; i < areas.length; i++) {
    const a = areas[i];
    if (isStrike(a) && !a.reach) areas[i] = { ...a, radius: Math.round((a.radius + STRIKE_REACH) * 100) / 100, reach: STRIKE_REACH };
  }
  return areas;
}

// Iteration 28: shots that blow up where they come down. A Dark Elemental's Barrage (history/Ai_Mon_DarkCave_DarkElemental_
// Barrage_Arrow.cs) is a StandardProjectile that plays its red circle as a plain effect at the point it is thrown at
// (FxPlayNew(telegraph, info.point)) and, on landing, hurts everyone in its `range` collider - nothing en route. /threats
// lists the arrow as a shot (its own small collision radius, `miss` against the hero's line) and not its circle: runs
// 035-040 took 780 from it in 16 "took" lines, every fight line beside them "N shots, 0 red areas" (run-040's DarkCave
// rooms: 24-102 a line while circling), the most any ordinary monster dealt. Decompiled (history/it28, IL of
// Dew.Contents): the same shape - `range` + a telegraph at the landing point - in the Orb Spitter's orbs, Stella Matter's
// throw and death, Big Baam's ranged attack, the Wretched Artillery's missiles and Erebos's Star Rain. So each such shot
// (not homing) is a strike at the end of its flight - position + heading x remaining - landing in remaining / speed s,
// its radius the projectile's own `range` (lobRadius, read once per type) or LOB_R until read; the shot itself is dropped
// from the dodge (it hurts only where it lands). Not added when /threats already lists a strike of that type there (the
// mod's reader, proposals/iter-28-mod.md). Pure.
const LOBBED = /DarkElemental_Barrage_Arrow|MiniBoss_OrbSpitter_Orb|Sky_StellaMatter_(Throw|Die)|Sky_BigBaam_RangedAtk|WretchedArtillery_BarrageAtk_Missile|BossErebos_StarRain_Instance/;
const LOB_R = 2.5;
const lobR = new Map();   // projectile type -> its range's radius (lobRadius)
function lobbedAreas(shots, listed = [], radii = lobR) {
  const areas = [], ids = new Set();
  for (const s of shots || []) {
    if (!s || s.homing || !LOBBED.test(s.type || '') || !s.position || !s.heading || !(s.remaining >= 0)) continue;
    ids.add(s.id);
    const centre = { x: s.position.x + s.heading.x * s.remaining, z: s.position.z + s.heading.z * s.remaining };
    if ((listed || []).some(a => a && a.type === s.type && a.centre && dist(a.centre, centre) < 1.5)) continue;
    const left = Math.round(s.remaining / Math.max(0.5, s.speed || 10) * 100) / 100;
    areas.push({ shape: 'strike', centre, radius: radii.get(s.type) || LOB_R, inner: 0, angle: 360, fill: Math.round(Math.max(0, Math.min(1, 1 - left / 2)) * 100) / 100,
      left, by: null, type: s.type, lobbed: true, shot: s.id });
  }
  return { areas, ids };
}
// Iteration 28: timed red on a straight walk from a to b (the fight's walk-up, the quiet walks, the loot and exit walks):
// the first area - not a pool or keep-out ring (their own checks), not an untimed blob (no fuse to wait out) - landing
// within maxLeft s whose shape the segment passes within `margin` of (a box: sampled along it; a slice: its whole circle).
// `inside`: a stands in it.
// null when none. Pure.
function redInWay(areas, a, b, maxLeft = 2.5, margin = 0.6) {
  let best = null;
  for (const r of areas || []) {
    if (!r || !r.centre || r.pool || r.keepOut || r.timeless || r.shape === 'safe' || r.shape === 'zone' || !(r.left >= 0) || r.left > maxLeft) continue;
    const inside = areaDepth(a, r, 0.4) > 0;
    let on = inside;
    if (!on && b) {
      if (r.shape === 'box' || r.shape === 'poly') { const n = Math.max(2, Math.ceil(dist(a, b) / 0.5)); for (let i = 1; i <= n && !on; i++) on = areaDepth({ x: a.x + (b.x - a.x) * i / n, z: a.z + (b.z - a.z) * i / n }, r, margin) > 0; }
      else on = segDist(r.centre, a, b) < (r.radius || 0) + margin;   // a slice as its whole circle: the walk may turn into it
    }
    if (on && (!best || r.left < best.area.left)) best = { area: r, inside };
  }
  return best;
}
// A point `len` m along the straight way from a to b (b itself when nearer). Pure.
const alongTo = (a, b, len) => { const d = dist(a, b); return d <= len ? b : { x: a.x + (b.x - a.x) * len / d, z: a.z + (b.z - a.z) * len / d }; };
// The red a walk outside the fight's own moves weighs (redInWay): /threats' areas as fight() reads them (no zones - the
// pools have their own waits - no safe circles, no Blackhole) and the lobbed shots' landings, strikes widened by their
// reach. th: a /threats reply. Pure.
function walkReds(th, nowMs = Date.now()) {
  if (!th) return [];
  const raw = (th.areas || []).filter(a => a && a.centre && a.shape !== 'zone' && a.shape !== 'safe' && a.shape !== 'blast' && !/BossNyx_Blackhole/.test(a.type || ''));
  return addReach(readAreas(raw.concat(lobbedAreas(th.projectiles || [], th.areas || []).areas), new Map(), nowMs));
}
// A point 1.5 m outside area a, straight away from its centre from p (a box: its half-diagonal). Pure.
function outOf(a, p) {
  const r = (a.shape === 'box' || a.shape === 'poly') && a.corners ? Math.max(...a.corners.map(c => dist(c, a.centre))) : (a.radius || 0);
  const d = dist(p, a.centre), k = (r + 1.5) / Math.max(0.1, d);
  return d < 0.1 ? { x: a.centre.x + r + 1.5, z: a.centre.z } : { x: a.centre.x + (p.x - a.centre.x) * k, z: a.centre.z + (p.z - a.centre.z) * k };
}

// ----- iteration 35: the endgame - zone 3 (Despair, Azurak) and zone 4 (Primus); pure -----
// No run has reached zone 3; all of this is from the decompiled game (history/it35/dec, repo endgame.md) and the mod's
// /threats readers (proposals/iter-35-mod.md). Without those readers nothing here fires: no /threats area of these kinds.
// Azurak's Roar (Ai_Mon_Despair_BossAzurak_RoarAtk): pillars rise round the arena, then he roars atkCount times; everyone
// within 50 m is hit and knocked back unless standing in a pillar's shadow (Se_..._RoarAtk_SafeZoneSpawner's range, turned
// away from him - also behind each monster spawner). /threats lists the shadows as `safe` circles, left = until the next
// roar. The same walk as White Night's Cataclysm (cataclysmStep: the quickest circle, walked or dashed into, stood in):
// { points, radius (the smallest), left (the soonest), how } or null.
const ROAR_SAFE = /BossAzurak_RoarAtk_SafeZone/;
// What gives the shelter is not shot while the roar is on (a spawner or pillar that dies takes its shadow with it).
const ROAR_SHELTER = /AzurakMonsterSpawner|AzurakRollPillar|AzurakRoarPillar/;
function roarSafe(areas) {
  const s = (areas || []).filter(a => a && a.shape === 'safe' && a.centre && ROAR_SAFE.test(a.type || '') && a.radius > 0.3);
  if (!s.length) return null;
  return { points: s.map(a => ({ x: a.centre.x, z: a.centre.z })), radius: Math.min(...s.map(a => a.radius)),
    left: Math.min(...s.map(a => typeof a.left === 'number' ? a.left : 2)), how: '/threats (Roar)' };
}
// Primus's phase-change blast (/threats `blast`: Se_Mon_Primus_BossPrimusAeron_PhaseSwitcher's explodeRange round it, set off
// by the blow that brings it to 0 in Force or Adapt - no damage, a knockback and a 2.5 s stun; Primus is invulnerable and
// does nothing through the change, so the stun costs little). Kept out of like a chaser orb's ring (keepOut: the cells
// weigh it, no dash for it) once Primus is within BLAST_FILL of the change, and only when it is small enough to shoot from
// outside (BLAST_MAX: the basic attack reaches ~8.65 m) - a bigger one is taken as it comes.
const BLAST_FILL = 0.9, BLAST_MAX = 7.5, BLAST_PAD = 0.8;
function blastKeep(areas) {
  return (areas || []).filter(a => a && a.shape === 'blast' && a.centre && a.fill >= BLAST_FILL && a.radius > 0 && a.radius <= BLAST_MAX)
    .map(a => ({ shape: 'circle', centre: a.centre, radius: Math.round((a.radius + BLAST_PAD) * 100) / 100, inner: 0, angle: 360, fill: 1, left: 99,
      keepOut: true, by: a.by, type: a.type }));
}
// Despair's rooms are islands joined by jump shrines (Shrine_Despair: locked until its part of the room is cleared, then
// throws the hero to its targetPos - Se_Shrine_Despair_Teleport, invulnerable and dazed in flight; a Shrine_Despair_Vestige
// at the far end throws back to its `destination`). A goal the navmesh cannot reach from here (a far enemy, the next part
// of the room, the exit) is reached through one: the usable shrine, reachable on foot (`reach`: its walk, null = none), whose
// landing (`dest`) is at least HOP_GAIN m nearer the goal than the hero is - the one landing nearest it (+ 0.3 x the walk).
// Not one used in the last HOP_AGAIN ms (a Vestige leads straight back). Returns a candidate or null.
const DESPAIR_SHRINE = /^Shrine_Despair(_Vestige)?$/;
const HOP_GAIN = 6, HOP_AGAIN = 20000;
function despairPick(me, goal, cands, now = Date.now()) {
  let best = null;
  for (const c of cands || []) {
    if (!c || !c.dest || typeof c.reach !== 'number' || (c.usedAt && now - c.usedAt < HOP_AGAIN)) continue;
    if (dist(c.dest, goal) > dist(me, goal) - HOP_GAIN) continue;
    const score = dist(c.dest, goal) + 0.3 * c.reach;
    if (!best || score < best.score) best = { ...c, score };
  }
  return best;
}
// Iteration 52 (run-056's Room_Despair_Combat_0_4): the hero on the upper platform, the room's goals all cut off - two parts
// still to fight at ground level ~60 m off and a third, (-15.2, 43.0), on another island. Asked for the nearest ground part,
// the one walkable shrine (#7025) "landed 110-114 m from it" - no help - so "quiet but exit closed", stuck; used by the
// monitor, it landed 8 m from (-15.2, 43.0). A shrine is judged against the goal asked for first; when none helps there,
// against the room's other parts still to fight (`others`, while the room is neither cleared nor open) - the best of the
// first of them it helps. Returns the candidate with its `goal` (and `other`: not the one asked for), or null.
function despairPickAny(me, goal, others, cands, now = Date.now()) {
  const p = goal ? despairPick(me, goal, cands, now) : null;
  if (p) return { ...p, goal, other: false };
  let best = null;
  for (const g of others || []) {
    if (!g || (goal && dist(g, goal) < 4)) continue;
    const q = despairPick(me, g, cands, now);
    if (q && (!best || q.score < best.score)) best = { ...q, goal: g, other: true };
  }
  return best;
}
// A box much longer along its facing than wide (/threats' corners: 0->1 across, 3->0 along): a sweep (plan()'s SWEEP_W).
const SWEEP_T = 2, SWEEP_W = 3;
const sweepLong = a => { const c = a.corners; return dist(c[3], c[0]) > 2 * dist(c[0], c[1]); };
// Ground that hurts and moves (Primus's Pyranas fireballs in its Doom: TickDamageInstances that chase the hero at up to 30
// m/s): not a pool where it was (notePools would leave a trail of them), a ring kept out of where it is now.
const MOVING_ZONE = /PyranasFireball/;
const movingZones = areas => (areas || []).filter(a => a && a.shape === 'zone' && a.centre && MOVING_ZONE.test(a.type || ''))
  .map(a => ({ shape: 'circle', centre: a.centre, radius: Math.round(((a.radius || 1) + 1) * 100) / 100, inner: 0, angle: 360, fill: 1, left: 99, keepOut: true, by: a.by, type: a.type }));
// The endgame's /threats kinds, each logged once a run the first time it is listed (fight()).
const ENDGAME_AREA = /BossAzurak|Mon_Despair_|BossPrimus|Primus_/;
const endgameSaid = new Set();
// ----- end of iteration 35's pure part

// ----- iteration 48: the jump shrine only on quiet ground (run-053's death); pure -----
// run-053, Room_Despair_Combat_3_3: entered at 1353/1376, nobody in sight yet; the quiet branch's goal (39.3 m off) was on
// another island, so despairHop sent the hero on the game's own 13.3 m walk to a jump shrine (/hero/interact) and then only
// watched for the flight - no /damage, no /entities, no /threats - until its 9 s ran out. The walk woke the island's fight: 9
// monsters round the hero, a Paralytic Bug's Paralyze stunned it (the interact walk ends there, nothing sent again), and the
// whole 1353 went in ~6 s (Dread Bug dash attacks 139 x3 + 109, the Paralytic Bug's 99 x3 + DoTs, the Unstable Rat's 48 x2)
// with nothing logged. Now:
// - hopGate, before the walk: held while an enemy is within HOP_CLEAR of the hero, and while a part of the room still to
//   fight (an inactive combat area) is reachable on foot - that part first. Unless the hero must flee: hp at or under
//   HOP_FLEE_HP, the landing with no enemy within HOP_CLEAR of it, the shrine within HOP_FLEE_WALK.
// - hopDanger, on each look of the walk (every ~250 ms until the flight starts - invulnerable from then on): a new blow taken
//   (not a tick of a DoT), an enemy within HOP_CLEAR, standing in red, a shot about to land -> the walk is stopped and the
//   fight loop takes over; the same flee exception (the shrine within HOP_FLEE_LEFT).
const HOP_CLEAR = 12, HOP_FLEE_HP = 0.35, HOP_FLEE_WALK = 8, HOP_FLEE_LEFT = 4, HOP_SHOT_ETA = 1.2;
const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
// s: { hpFrac, near (enemies within HOP_CLEAR of the hero), landingNear (of the landing; null = not known), foot (the walk to
// the nearest part still to fight reachable on foot, m; null = none), reach (the walk to the shrine, m) }.
function hopGate(s) {
  const fleeOk = s.hpFrac <= HOP_FLEE_HP && s.landingNear === 0 && typeof s.reach === 'number' && s.reach <= HOP_FLEE_WALK;
  if (s.near > 0) {
    if (fleeOk) return { go: true, flee: true, why: `fleeing: hp ${Math.round(s.hpFrac * 100)}%, ${plural(s.near, 'enemy', 'enemies')} within ${HOP_CLEAR} m, nobody within ${HOP_CLEAR} m of the landing` };
    return { go: false, why: `${plural(s.near, 'enemy', 'enemies')} within ${HOP_CLEAR} m` };
  }
  if (typeof s.foot === 'number') return { go: false, foot: true, why: `a part of the room still to fight is reachable on foot (${s.foot.toFixed(1)}m walk) - that part first` };
  return { go: true, flee: false, why: null };
}
// A look during the walk to the shrine. s: { hpFrac, near, landingNear, left (m to the shrine), hits (/damage since the last
// look), threats (/threats) }. null = walk on; { stop, why } - stop false: danger, but fleeing through it.
function hopDanger(s) {
  const why = [];
  const blows = (s.hits || []).filter(h => h && !h.overTime && (h.amount || 0) > 0);
  if (blows.length) why.push(`took ${Math.round(blows.reduce((t, h) => t + h.amount, 0))} (${[...new Set(blows.map(h => h.caster || h.by || '?'))].join(', ')})`);
  if (s.near > 0) why.push(`${plural(s.near, 'enemy', 'enemies')} within ${HOP_CLEAR} m`);
  const t = s.threats || {};
  const red = (t.areas || []).find(a => a && a.inside && a.shape !== 'safe');
  if (red) why.push(`in a ${red.shape} of ${red.by || red.type || '?'}`);
  const shot = (t.projectiles || []).find(p => p && typeof p.eta === 'number' && p.eta <= HOP_SHOT_ETA && (p.homing || p.miss <= (p.radius || 0.5) + 1));
  if (shot) why.push(`a shot (${shot.type || '?'}) in ${shot.eta}s`);
  if (!why.length) return null;
  const flee = s.hpFrac <= HOP_FLEE_HP && s.landingNear === 0 && typeof s.left === 'number' && s.left <= HOP_FLEE_LEFT;
  return { stop: !flee, why: why.join(', ') + (flee ? ` - fleeing on: hp ${Math.round(s.hpFrac * 100)}%, the shrine ${s.left.toFixed(1)}m off, the landing clear` : '') };
}
// ----- end of iteration 48's pure part

// ----- iteration 50: Azurak's Atk where it lands, and its trail (run-054); pure -----
// run-054 entered Primus at 509/1096; 476 of the loss was one Azurak Atk (InitDamage 238 + SubDamage 238: 397 -> 74), and
// nothing heals between his room and Primus. His Atk hit in every run that met him - 047: 222 + 222 + 222 + 111, 050: 222,
// 051: 111, 052: 194 + 97 + 97, 054: 238 + 238 (~400 a run). The mod's wind-up reader (readers < 50) drew it as a 3.5 m strike
// ON Azurak, and the bot, holding 7-10 m off, was always outside it; the blow landed 3.3-4.9 m from the hero. Decompiled
// (history/it35/dec, it50): At_Mon_Despair_BossAzurak_Atk is a plain AttackTrigger - its InitDamage spawns where Azurak stands,
// turned toward the point it aims at (the hero's place as the wind-up begins) - and the blow is the instance's `range`, which
// sits ahead of him in the prefab. Fitting the traces of runs 050-054 (the hero's place at the wind-up's start and at the
// blow, Azurak's distance then, the blow's distance from the hero) puts it ~6 m ahead (5-7). So, without readers 50 (the mod
// then places it itself), the wind-up's strike is moved AZ_OFF m from Azurak toward where the hero stood when it began (the
// hero's track, back-dated by the wind-up's fill). Its trail (Ai_..._Atk_SubSpawner x 2-3 from the blow's point): every
// AZ_STEP s a SubDamage (r 1.65) AZ_GAP m farther, turned up to AZ_TURN deg toward the nearest hero, AZ_STEPS steps - 8.6 m/s,
// faster than the hero; /threats lists each step as it lands (0.01-0.03 s left: nothing to dodge). Its next step is known from
// the last two, so it is listed ahead as a strike (and the one after it, fainter).
const AZ_WINDUP = /Windup_At_Mon_Despair_BossAzurak_Atk$/, AZ_INIT = /BossAzurak_Atk_InitDamage/, AZ_SUB = /BossAzurak_Atk_SubDamage/;
const AZ_OFF = 6, AZ_READERS = 50, AZ_GAP = 3, AZ_TURN = 20, AZ_STEP = 0.35, AZ_STEPS = 8, AZ_SUB_R = 1.65, AZ_TRACK = 2500;
const azYaw = (dx, dz) => Math.atan2(dx, dz) * 180 / Math.PI;
// Mathf.MoveTowardsAngle: from a toward b by at most max deg.
function azTurn(a, b, max) { const d = ((b - a + 540) % 360) - 180; return a + Math.max(-max, Math.min(max, d)); }
// One look. areas: fight()'s list (strikes not widened yet) - the wind-up's strike is moved in place and the predicted steps
// returned; st: kept by the caller per fight; o: { now (ms), me (the hero), boss (Azurak's entity or null), readers }.
// Returns { add: predicted steps (strikes), notes: [{ kind: 'windup', centre, d, drawn } | { kind: 'trail', n }] }.
function azurakAtk(areas, st, o) {
  const now = o.now, add = [], notes = [];
  st.track = (st.track || []).filter(p => now - p.t <= AZ_TRACK);
  if (o.me) st.track.push({ t: now, x: o.me.x, z: o.me.z });
  const w = areas.findIndex(a => a && a.centre && isStrike(a) && AZ_WINDUP.test(a.type || ''));
  if (w >= 0 && o.boss && o.boss.position && !(o.readers >= AZ_READERS)) {
    const a = areas[w];
    if (!st.cast || now > st.cast.until + 400) {
      const fill = Math.max(0, Math.min(0.95, a.fill || 0)), left = Math.max(0, a.left || 0);
      const back = now - fill * (left / (1 - fill)) * 1000;
      let from = null;
      for (const p of st.track) if (!from || Math.abs(p.t - back) < Math.abs(from.t - back)) from = p;
      from = from || o.me;
      const b = o.boss.position, d = dist(b, from), k = d > 0.1 ? Math.min(AZ_OFF, d) / d : 0;
      st.cast = { centre: { x: b.x + (from.x - b.x) * k, z: b.z + (from.z - b.z) * k }, until: now + left * 1000 };
      notes.push({ kind: 'windup', centre: st.cast.centre, d, drawn: dist(a.centre, b) });
    }
    st.cast.until = Math.max(st.cast.until, now + (a.left || 0) * 1000);
    areas[w] = { ...a, centre: st.cast.centre, moved: true };
  }
  // The blow and its trail's steps as /threats lists them.
  st.steps = (st.steps || []).filter(s => now - s.t < 1500);
  for (const a of areas) {
    if (!a || !a.centre || !isStrike(a)) continue;
    if (AZ_INIT.test(a.type || '')) {
      if (!st.init || dist(st.init.c, a.centre) > 1 || now - st.init.t > 1500) st.init = { c: { x: a.centre.x, z: a.centre.z }, t: now, n: 0 };
    } else if (AZ_SUB.test(a.type || '') && !a.predicted) {
      if (st.steps.some(s => dist(s.c, a.centre) < 0.5)) continue;
      let prev = null;
      for (const s of st.steps.concat(st.init ? [st.init] : [])) {
        const g = dist(s.c, a.centre), dt = now - s.t;
        if (g < AZ_GAP - 1 || g > AZ_GAP + 1 || dt < 150 || dt > 800) continue;
        if (!prev || s.t > prev.t) prev = s;
      }
      st.steps.push({ c: { x: a.centre.x, z: a.centre.z }, t: now, prev: prev ? prev.c : null, n: prev ? prev.n + 1 : 1 });
    }
  }
  // Each chain's tip: its next step (and the one after).
  let n = 0;
  for (const s of st.steps) {
    if (!s.prev || s.n >= AZ_STEPS || now - s.t > AZ_STEP * 1000 + 250) continue;
    if (st.steps.some(q => q !== s && q.prev && dist(q.prev, s.c) < 0.3)) continue;
    let yaw = azYaw(s.c.x - s.prev.x, s.c.z - s.prev.z), c = s.c;
    const left0 = Math.max(0.05, AZ_STEP - (now - s.t) / 1000);
    for (let j = 0; j < 2 && s.n + j < AZ_STEPS; j++) {
      if (o.me) yaw = azTurn(yaw, azYaw(o.me.x - c.x, o.me.z - c.z), AZ_TURN);
      c = { x: c.x + Math.sin(yaw * Math.PI / 180) * AZ_GAP, z: c.z + Math.cos(yaw * Math.PI / 180) * AZ_GAP };
      const left = Math.round((left0 + j * AZ_STEP) * 100) / 100;
      add.push({ shape: 'strike', centre: c, radius: AZ_SUB_R, inner: 0, angle: 360, fill: j ? 0.3 : Math.round(Math.max(0, Math.min(1, 1 - left / AZ_STEP)) * 100) / 100,
        left, by: 'Azurak', type: `Ai_Mon_Despair_BossAzurak_Atk_SubDamage (step ${s.n + j + 1}, predicted)`, predicted: true });
    }
    n++;
  }
  if (n) notes.push({ kind: 'trail', n });
  return { add, notes };
}
// ----- end of iteration 50's Azurak part

// ----- iteration 39: Primus, from run-047 (the first run to reach it; dead 8 s in); pure -----
// Its Force Atk hit for 519 (45% of max hp) at 4.0 m while /threats listed only its drawings - blobs of 10-18 m with no timer -
// so the last dash charge was held ("a blob, no timer"); then a Jump Attack cone hit for 593 at 9.1 m from the landing with
// nothing listed. Both blows have a polygon collider, which no /threats reader listed. The mod's readers 39
// (proposals/iter-39-mod.md) list Primus's polygons from their wind-up (Force Atk, Swipe, Rage's swipes: `poly`), the cones
// where they will hit once it lands (`poly`), and before that their whole reach round the landing point (a `strike`, type
// "..._ConeInstance (predicted)", or "Windup_At_..._JumpAttack" while it winds up). /threats' `readers` says the build has them.
// With them Primus's blobs are only drawings of blows listed with their real shape and fuse: dropped (primusDrawings) - a blob
// gets a dash (run-047 10:32:38: 9.4 m "in" a 12.8 m one, nothing hit) or holds the last charge (10:32:39: held, then 519).
// Without them Primus is a blind boss: its closing in is the only warning, so the last charge is not kept (primusBlind).
const PRIMUS = /BossPrimus/, PRIMUS_READERS = 39;
const primusReaders = th => !!th && typeof th.readers === 'number' && th.readers >= PRIMUS_READERS;
function primusDrawings(areas, th, bosses) {
  if (!primusReaders(th)) return areas;
  const names = new Set((bosses || []).filter(e => PRIMUS.test(e.type || '')).map(e => e.name).filter(Boolean));
  if (!names.size) return areas;
  return areas.filter(a => !(a && a.shape === 'blob' && names.has(a.by)));
}
let primusSaid = null;   // the Primus line logged for this one (its id)
let infernusSaid = null;   // iteration 51: the Infernus line logged for this one (its id)
// Iteration 42: Primus's phase by the phase changes seen (primusStage without the mod's `primus`), and its Adapt Atk's drawings seen.
const primusSt = { id: null, stage: 0 };
// Iteration 45: the Force phase's pacing (primusPace), per Primus.
const paceSt = {};
const adaptSeen = new Map();
const primusBlind = (th, bosses) => (bosses || []).some(e => PRIMUS.test(e.type || '')) && !primusReaders(th);
// A melee boss that dashes at the hero when > 4 m off (its Force AI) and swings at 4 m: kept at the edge of the basic attack's
// reach (PRIMUS_R x range, ~8 m; the usual boss range is 0.85 x = 7.35), its melee weighed out to PRIMUS_MELEE (5.5 as usual).
const PRIMUS_R = 0.92, PRIMUS_MELEE = 6.5;
// Iteration 42: `stage` (primusStage) - in the Adapt phase (1) Primus has no melee blow, its melee weighed to PRIMUS_MELEE_ADAPT.
function primusOpts(opts, target, range, stage = 0) {
  if (!target || !PRIMUS.test(target.type || '') || opts.desired || opts.keep || opts.approach) return opts;
  return { ...opts, desired: Math.round(PRIMUS_R * range * 100) / 100, bossMelee: stage === 1 ? PRIMUS_MELEE_ADAPT : PRIMUS_MELEE };
}
// Iteration 51: Infernus's Atk (its attack, an InstantDamageInstance never listed - its `range` is a POLYGON, read out of the
// bundle: a wedge from its centre, (+-3.6, 1.5) (+-3.9, 3.7) (+-2.25, 6.4) (0, 7.42), ~120 deg wide) hit at 5.6-7.15 m from it
// (probe.jsonl, runs 042-053: 7 hits, 64-128 each + the burn it lights, ~1.5-3x that). The fight held it at 0.85 x 8.65 =
// 7.35 m, inside the wedge. So Infernus is kept at INFERNUS_R x the basic attack's range (~8.2 m: past the wedge + the
// hero's body, still in reach), its melee weighed out to INFERNUS_MELEE - as Primus is; the shield's keep and the breath's
// close circle (BREATH_R) as before. Pure.
const INFERNUS = /BossInfernus/, INFERNUS_R = 0.95, INFERNUS_MELEE = 7.9;
function infernusOpts(opts, target, range) {
  if (!target || !INFERNUS.test(target.type || '') || opts.desired || opts.keep || opts.approach) return opts;
  return { ...opts, desired: Math.round(INFERNUS_R * range * 100) / 100, bossMelee: INFERNUS_MELEE };
}
// ----- end of iteration 39's pure part

// ----- iteration 41: the Phase Bug (run-049's death); pure -----
// Room_Despair_Combat_1_3, a miniboss Phase Bug (Mon_Despair_Displacer, 4099 hp): 897 -> 0 in ~6 s. Its Blink (invulnerable,
// unseen, toward the hero; an egg where it lands) ended at 8.7 m and the bot at once charged Precision Shot for 0.95 s (the
// full charge: nobody within 6 m, no red, no shot); in that blind second the bug dashed 10.7 m onto the hero (280). Then two
// of its Spinning Arrows (346 at 1 m from it, 332 at ~9 m). Decompiled (history/it35/dec, history/it41): the Displacer
// dashes (DashAttackInstance: dash.distance along the direction fixed at the cast's start) whenever the hero is in the
// trigger's range - a miniboss has 3 charges and half the wind-up (~0.67 s by its box in the trace; the box the game draws is
// in /threats, and the bot walked out of the next two). Se_MiniBoss_SpinningArrow shoots an arrow every 0.2 s from the
// carrier, each 13 deg further round (~65 deg/s, a turn in ~5.5 s), the first away from the hero, none while the carrier
// dashes, blinks or spawns (and 0.5 s after); an arrow's first hit on the hero in 2 s deals 1.65x, the others 0.25x (base
// ~210 in zone 3). The hero's dash (Ai_GenericDodge) is uncollidable for 70% of its flight (12 m/s: ~0.3 s for 5 m) - what the
// arrows pass through.
// 1. No blind charge within a dasher's reach (dasherNear): Precision Shot charged no longer than P_MIN while a Displacer that
//    can dash stands within its reach (the mod's `dashers`, readers 41; else DASHER_REACH by type).
const DASHER = /Mon_Despair_Displacer/;
const DASHER_REACH = 12;   // run-049: 10.7 m dashed (7.4,-12.9 -> 0.8,-21.3), + the lane's half width and a margin
function dasherNear(me, entities, dashers = null) {
  let best = null;
  if (Array.isArray(dashers) && dashers.length) {
    for (const d of dashers) {
      if (!d || !d.position) continue;
      const reach = (d.reach > 1 ? d.reach : DASHER_REACH - 1) + 1, dd = dist(me, d.position);
      if (dd > reach || (d.charges === 0 && !d.casting)) continue;
      if (!best || dd < best.d) best = { d: dd, name: d.by || 'a dasher', reach, charges: d.charges, casting: !!d.casting, from: 'mod' };
    }
    return best;
  }
  for (const e of entities || []) {
    if (!e || e.alive === false || !e.position || !DASHER.test(e.type || '')) continue;
    const dd = dist(me, e.position);
    if (dd <= DASHER_REACH && (!best || dd < best.d)) best = { d: dd, name: e.name || e.type, reach: DASHER_REACH, from: 'type' };
  }
  return best;
}
// 2. The spinning arrows as a stream (spinStream): the carrier, the way the stream points now (theta, deg of atan2(z, x)), the
//    way it turns (turn: +1 the angle grows - as orbitSide), its speed (omega, deg/s), the arrows' speed, reach and radius.
//    From the mod's `spinners` (readers 41) when listed; else read off the arrows in flight - each flies straight out from where
//    the carrier stood: the newest (nearest the carrier) gives the way it points, the next ones the way it turns.
const SPIN_ARROW = /SpinningArrow_Arrow/;
const SPIN_STEP = 13, SPIN_INTERVAL = 0.2, SPIN_FRONT = 0.5, SPIN_BODY = 0.4;
const SPIN_LINE = 1.5;   // m: how near the carrier an arrow's line has to pass to be one of its
function spinStream(shots, entities, spinners = null) {
  const ms = (spinners || []).filter(s => s && s.centre && s.facing && s.started && s.speed > 0);
  if (ms.length) {
    const s = ms.reduce((b, x) => !b || x.reach > b.reach ? x : b, null);
    const omega = s.interval > 0.01 && s.step > 0 ? s.step / s.interval : SPIN_STEP / SPIN_INTERVAL;
    const theta = angDeg(s.facing) + (s.turn || 1) * omega * Math.max(0, (s.interval || SPIN_INTERVAL) - (s.next >= 0 ? s.next : 0));
    return { centre: { x: s.centre.x, z: s.centre.z }, theta, turn: s.turn || 1, omega, speed: s.speed, reach: s.reach, radius: s.arrowRadius || 0.25,
      pausedFor: s.pausedFor || 0, by: s.by, from: 'mod' };
  }
  const arrows = (shots || []).filter(s => SPIN_ARROW.test(s.type || '') && s.position && s.heading && !s.homing);
  if (arrows.length < 2) return null;
  let carrier = null;
  for (const e of entities || []) {
    if (!e || !e.position || e.alive === false) continue;
    // The three newest (nearest along their lines) only: the carrier walks, and the older arrows left from where it stood.
    const near = [];
    for (const s of arrows) {
      const ax = s.position.x - e.position.x, az = s.position.z - e.position.z, along = ax * s.heading.x + az * s.heading.z;
      if (along >= -0.5) near.push({ along, off: Math.abs(ax * s.heading.z - az * s.heading.x) });
    }
    near.sort((p, q) => p.along - q.along);
    const top = near.slice(0, 3);
    if (top.length < 2) continue;
    const off = top.reduce((s, x) => s + x.off, 0) / top.length;
    if (off > SPIN_LINE) continue;
    const sc = off - (isBossE(e) ? 0.5 : 0);
    if (!carrier || sc < carrier.sc) carrier = { e, sc };
  }
  if (!carrier) return null;
  const C = carrier.e.position;
  const list = arrows.map(s => ({ s, r: (s.position.x - C.x) * s.heading.x + (s.position.z - C.z) * s.heading.z, b: angDeg(s.heading) }))
    .filter(x => x.r >= -0.5).sort((p, q) => p.r - q.r);
  const speed = list.map(x => x.s.speed).filter(v => v > 0).sort((p, q) => p - q)[Math.floor(list.length / 2)] || 10;
  // The way it turns: from each newer arrow to the next older one, the angle it has turned since (13 deg an arrow).
  let sum = 0, rate = [];
  for (let i = 0; i + 1 < list.length; i++) {
    const dr = list[i + 1].r - list[i].r, db = angDiff(list[i].b, list[i + 1].b);
    if (dr < 0.3 || Math.abs(db) < 1 || Math.abs(db) > 90) continue;
    sum += Math.sign(db); rate.push(Math.abs(db) / (dr / speed));
  }
  if (!sum) return null;
  rate.sort((p, q) => p - q);
  const est = rate[Math.floor(rate.length / 2)];
  const omega = est >= 30 && est <= 130 ? est : SPIN_STEP / SPIN_INTERVAL;
  const turn = Math.sign(sum), newest = list[0];
  const theta = newest.b + turn * omega * Math.max(0, newest.r - SPIN_FRONT) / speed;
  const reach = Math.max(...list.map(x => x.r + (x.s.remaining || 0)));
  const radius = Math.max(...list.map(x => x.s.radius || 0.25));
  return { centre: { x: C.x, z: C.z }, theta, turn, omega, speed, reach, radius, pausedFor: 0, by: carrier.e.name, from: 'shots' };
}
// When the stream's arrow meets the point p: t (s; Infinity beyond its reach), delta (deg it still turns before it points at
// p), rho (m from the carrier). An arrow already shot that way and still short of p counts first. Pure.
function spinMeet(sp, p) {
  if (!sp || !p) return { t: Infinity, delta: null, rho: null };
  const rho = dist(p, sp.centre);
  if (rho > sp.reach + sp.radius + SPIN_BODY) return { t: Infinity, delta: null, rho };
  const psi = angDeg({ x: p.x - sp.centre.x, z: p.z - sp.centre.z });
  const delta = ((sp.turn * (psi - sp.theta)) % 360 + 360) % 360;
  const fly = Math.max(0, rho - SPIN_FRONT) / sp.speed;
  const past = -(360 - delta) / sp.omega + fly;   // the arrow shot that way last time round: still to come?
  if (past >= 0 && past < fly) return { t: past, delta, rho };
  let fire = delta / sp.omega;
  if (fire < (sp.pausedFor || 0)) fire += 360 / sp.omega;   // held: none that way this time round
  return { t: fire + fly, delta, rho };
}
// The sweep of the next SPIN_AHEAD s, from SPIN_TRAIL deg behind the stream: a slice kept out of (keepOut, left 99: walked out
// of, not dashed for - the dash is spinDash's), so the walks go round ahead of it rather than into it.
const SPIN_AHEAD = 0.9, SPIN_TRAIL = 10;
function spinArea(sp) {
  const ahead = sp.omega * SPIN_AHEAD, span = ahead + SPIN_TRAIL;
  const mid = (sp.theta - sp.turn * SPIN_TRAIL + sp.turn * span / 2) * Math.PI / 180;
  return { shape: 'slice', centre: sp.centre, radius: Math.round((sp.reach + sp.radius + SPIN_BODY) * 100) / 100, inner: 0, angle: Math.round(span),
    facing: { x: Math.cos(mid), z: Math.sin(mid) }, fill: 0.5, left: 99, keepOut: true, by: sp.by, type: 'SpinningArrow_Stream' };
}
// The dash through the stream when it is about to meet the hero (within SPIN_DASH_T s: the dash's uncollidable ~0.3 s): back
// round the carrier against its turn by SPIN_GAIN deg at least - onto the side it has just swept, safe for most of a turn - or
// out of its reach; 3.5-5.5 m, with room, off the red, and where no arrow in flight is at the landing when the hero gets there
// (12 m/s). The most degrees gained, less for each metre nearer than SPIN_NEAR. null when none. Pure.
const SPIN_DASH_T = 0.4, SPIN_GAIN = 25, SPIN_NEAR = 4, SPIN_DASH_V = 12;
function spinDashCell(grid, me, sp, areas = [], shots = []) {
  if (!grid || !grid.reach || !sp) return null;
  const { origin, step, size, reach, clear } = grid;
  const wet = wetCells(grid), a0 = angDeg({ x: me.x - sp.centre.x, z: me.z - sp.centre.z });
  const arrows = (shots || []).filter(s => SPIN_ARROW.test(s.type || '') && s.position && s.heading);
  const out = sp.reach + sp.radius + SPIN_BODY;
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8 || clear[k] < 2 || wet(k)) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, p);
    if (md < 3.5 || md > 5.5) continue;
    if (areas.some(a => !a.keepOut && areaDepth(p, a, 0.4) > 0)) continue;
    const dC = dist(p, sp.centre), gone = dC > out;
    const gain = -sp.turn * angDiff(angDeg({ x: p.x - sp.centre.x, z: p.z - sp.centre.z }), a0);
    if (!gone && gain < SPIN_GAIN) continue;
    const at = md / SPIN_DASH_V;
    if (arrows.some(s => { const q = { x: s.position.x + s.heading.x * s.speed * at, z: s.position.z + s.heading.z * s.speed * at }; return dist(q, p) < (s.radius || 0.25) + SPIN_BODY + 0.3; })) continue;
    const score = (gone ? 60 : gain) - 6 * Math.max(0, SPIN_NEAR - dC) + 2 * Math.min(clear[k], 4);
    if (!best || score > best.score) best = { k, p, md, dC, gain, gone, clear: clear[k], score };
  }
  return best;
}
// ----- end of iteration 41's fight part

// ----- iteration 42: Primus's Adapt phase, from run-050 (the first run into it: dead in 15 s); pure -----
// run-050 cleared the Force phase with no damage (27 s), then 11.8 s of phase change, and Primus came back with 103918 hp (its
// Adaptation: +240% max health, the most - history/it35/dec Se_..._Adaptation: clamp(90 s / Force's combat time - 1, 15%,
// 300%) x 0.8). The hero died 15 s into Adapt: its Adapt Atk 281 + 5 x 43-51, 338 + 3 x 48, then 148; a Starfall 255.
// The Adapt Atk (Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk) is Adapt's basic attack, cast every ~3 s (and right after each of
// its dashes, which reset it): a channel with a drawing at the point it is cast at - the hero's place - then a bolt there. On
// landing it hits whoever is within groundHitChainRadius (3 m) of the point - the full blow; then every 0.3 s a next link, up to
// 6 in all: at the one it hit (homing, 15% each: the 43-51s) or, if it hit no one, at a point 2 m farther from Primus +- 1.5
// m (radius 3 again). run-050's probe: each drawing was two blobs of 7.08 m by Primus centred on that point, ~1.0-1.4 s before
// the bolt hit from exactly there (21.34,43.00 and 32.04,57.52; the hero 1.26 and 0.94 m from it). 7.08 is not its reach:
// Starfall's 3.54 m blobs sit on its 1.5 m strikes (2.36 x) and 7.08 = 2.36 x 3; the hero stood 5.6-6.0 m from the centre of
// the 11:22:15.1 drawing - inside the blob - and was not hit, and 4.7-5.0 m from the 11:22:21.4 one's landing, not hit by it
// either; its second ground link (29.08,55.71 - out from Primus, where the "boss close" dash had just taken the hero) hit 338.
// The mod's readers 42 list it (a strike from the wind-up, in flight, and the next links: proposals/iter-42-mod.md). Without
// them (primusAdapt): a blob by Primus of ADAPT_BLOB m, not on Primus, is a strike of radius / ADAPT_DRAW landing ADAPT_T s after
// it was first seen - a timed red, the dash's hard reason (the last charge too) - and its next two links are predicted
// outward from Primus (ADAPT_STEP m apart, ADAPT_SPREAD wider, ADAPT_LINK_T s apart), so the way out is not straight away.
const PRIMUS_ADAPT_READERS = 42;
const ADAPT_BLOB = [6.3, 7.8], ADAPT_DRAW = 2.36, ADAPT_T = 1.0, ADAPT_OFF = 2, ADAPT_STEP = 2, ADAPT_SPREAD = 0.75, ADAPT_LINK_T = 0.4;
const ADAPT_ATK = /BossPrimusAeron_Adapt_Atk/, PRIMUS_MELEE_ADAPT = 3.5;
// The chain's next links out from Primus (p the landing, from Primus's place), r0 the landing's radius. Pure.
function adaptLinks(p, from, r0, left, type) {
  const d = dist(p, from);
  if (d < 0.1) return [];
  const u = { x: (p.x - from.x) / d, z: (p.z - from.z) / d };
  const out = [];
  for (let k = 1; k <= 2; k++)
    out.push({ shape: 'strike', centre: { x: p.x + u.x * ADAPT_STEP * k, z: p.z + u.z * ADAPT_STEP * k }, radius: Math.round((r0 + ADAPT_SPREAD) * 100) / 100,
      inner: 0, angle: 360, fill: 0, left: Math.round((left + k * ADAPT_LINK_T) * 100) / 100, by: 'Primus', type: type + ' (next link)', predicted: true });
  return out;
}
// Primus's phase: 0 Force, 1 Adapt, 2 Rage - the mod's `primus.phase` (readers 42), else the phase changes seen (st.stage).
const PRIMUS_PHASES = { Force: 0, Adapt: 1, Rage: 2 };
function primusStage(th, st) {
  const p = th && th.primus && th.primus.phase;
  if (p && p in PRIMUS_PHASES) return PRIMUS_PHASES[p];
  return st && typeof st.stage === 'number' ? st.stage : 0;
}
// The Adapt Atk read from its drawing (no reader 42): raw /threats areas in, the same list out with each such blob a strike and
// its links added; seen: Map kept by the caller (a drawing's key -> when first seen). Returns { areas, casts: the new ones }.
function primusAdapt(raw, primus, th, seen, nowMs) {
  const casts = [];
  if (!primus || (th && typeof th.readers === 'number' && th.readers >= PRIMUS_ADAPT_READERS)) return { areas: raw, casts };
  const listed = raw.filter(a => a && isStrike(a) && ADAPT_ATK.test(a.type || ''));
  const out = [];
  const live = new Set();
  for (const a of raw) {
    const ours = a && a.shape === 'blob' && a.centre && a.by === primus.name && a.radius >= ADAPT_BLOB[0] && a.radius <= ADAPT_BLOB[1] &&
      dist(a.centre, primus.position) >= ADAPT_OFF;
    if (!ours) { out.push(a); continue; }
    const key = Math.round(a.centre.x * 2) + ':' + Math.round(a.centre.z * 2);
    if (live.has(key)) continue;   // its drawing is two blobs on one point
    live.add(key);
    if (listed.some(s => dist(s.centre, a.centre) < 1.5)) continue;
    let first = seen.get(key);
    if (first == null) { first = nowMs; casts.push({ centre: a.centre, drawn: a.radius }); }
    seen.set(key, first);
    const age = (nowMs - first) / 1000;
    const left = Math.round(Math.max(0, ADAPT_T - age) * 100) / 100;
    const r = Math.round(a.radius / ADAPT_DRAW * 100) / 100;
    out.push({ shape: 'strike', centre: a.centre, radius: r, inner: 0, angle: 360, fill: Math.round(Math.min(1, age / ADAPT_T) * 100) / 100, left,
      by: a.by, type: 'Primus_Adapt_Atk (its drawing)', drawn: a.radius });
    out.push(...adaptLinks(a.centre, primus.position, r, left, 'Primus_Adapt_Atk (its drawing)'));
  }
  for (const k of [...seen.keys()]) if (!live.has(k) && nowMs - seen.get(k) > 3000) seen.delete(k);
  return { areas: out, casts };
}
// An Adapt Atk bolt flying at a point (a /threats shot, not homing: the first bolt, or a link after a miss): a strike of
// ADAPT_R where it lands and its next links - unless the mod lists that landing (a strike of its type within 1.5 m). The
// homing links (the chain on the hero) stay shots. Returns { areas, ids } like lobbedAreas.
const ADAPT_R = 3;
function adaptBolts(shots, primus, listed = []) {
  const areas = [], ids = new Set();
  if (!primus) return { areas, ids };
  for (const s of shots || []) {
    if (!s || s.homing || !ADAPT_ATK.test(s.type || '') || !s.position || !s.heading || !(s.remaining >= 0)) continue;
    ids.add(s.id);
    const centre = { x: s.position.x + s.heading.x * s.remaining, z: s.position.z + s.heading.z * s.remaining };
    if ((listed || []).some(a => a && a.centre && isStrike(a) && ADAPT_ATK.test(a.type || '') && dist(a.centre, centre) < 1.5)) continue;
    const left = Math.round(s.remaining / Math.max(0.5, s.speed || 10) * 100) / 100;
    areas.push({ shape: 'strike', centre, radius: ADAPT_R, inner: 0, angle: 360, fill: 0.9, left, by: 'Primus', type: s.type, lobbed: true, shot: s.id });
    areas.push(...adaptLinks(centre, primus.position, ADAPT_R, left, s.type));
  }
  return { areas, ids };
}
// Starfall (Ai_..._Adapt_Starfall_Instance): each blow creeps toward the hero at 3 m/s until it lands (run-050's 255: the dash
// landed 2.3 m from one listed with ~0.2 s left - outside its 1.85 m - and it crept 1.2 m after the hero). Its radius is widened
// by what it can still creep (STAR_V x left, at most STAR_CAP): the ground that is safe to stand on until it lands. In place.
const STARFALL = /BossPrimusAeron_Adapt_Starfall_Instance/, STAR_V = 3, STAR_CAP = 1.5;
function starCreep(areas) {
  for (let i = 0; i < areas.length; i++) {
    const a = areas[i];
    if (!a || !isStrike(a) || a.creep != null || !STARFALL.test(a.type || '') || !(a.left >= 0)) continue;
    const c = Math.min(STAR_CAP, STAR_V * a.left);
    areas[i] = { ...a, radius: Math.round((a.radius + c) * 100) / 100, creep: Math.round(c * 100) / 100 };
  }
  return areas;
}
// ----- end of iteration 42's pure part

// ----- iteration 45: pacing Primus's Force phase (its Adaptation); pure -----
// Se_Mon_Primus_BossPrimusAeron_Adaptation (history/it35/dec): in the Force phase a clock runs while Primus is not damage-immune,
// no cutscene plays, and it TOOK damage (any hit, a burn's tick too) in the last 8 s. At the change to Adapt (after the floor
// pieces break) it gives max-health orbs: pct = clamp(90 / clock x 100 - 100, 15, 300) x 0.8, paid in orbs of 15% while
// pct > 0 - so ceil(pct / 15) x 15%: >= 75.8 s one orb (+15%), 65.5-75.8 two, 57.6-65.5 three, ... <= 23.1 s sixteen (+240%).
// The game's log says it (Player.log "[Adaptation] Combat Time: 19.2 seconds" = run-050: 16 orbs, 103918; "23.7" = run-051:
// 15 orbs, 99333). Rage keeps the bonus: max x (1 + bonus + 0.5), set to 5% of it, the rest a shield decaying over 90 s. The
// other orbs (Primus's speed +30% if it dealt < 1 AP-ratio of damage in Force, less the more it hit; damage +5% unless > 12)
// do not depend on the clock. Nothing else in Force runs on time: its AI casts by health only (Swipe, Dash, Atk above 80%;
// + Drop Giant Sword and Grab < 80%, Jump Attack < 65%, Gold Rain < 35%), no enrage, no adds, no regeneration - dealing
// nothing only stops the clock. So (primusPace): above 80% hold the skills and pace the basic attack - a hit on a budget line
// (PACE_LOSS lost by the burn's start) plus one whenever PACE_KEEP s went by with no drop in Primus's hp (the clock runs
// only with a hit in the last 8 s); burn when the clock + the burn's own time (hp / PACE_BURN) reaches PACE_T, or at
// PACE_HARD of its health. Adapt 35.1k instead of 99-104k (run-050/051), Rage 50.4k instead of 115-119k.
const ADAPT_WANT = 90, ADAPT_PCT = [15, 300], ADAPT_ORB = 15, ADAPT_K = 0.8, RAGE_BONUS = 0.5;
// The Adaptation's max-health bonus (a fraction) for a Force clock of t s.
function adaptBonus(t) {
  const pct = Math.min(ADAPT_PCT[1], Math.max(ADAPT_PCT[0], t > 0 ? ADAPT_WANT / t * 100 - 100 : Infinity)) * ADAPT_K;
  return Math.ceil(pct / ADAPT_ORB - 1e-6) * ADAPT_ORB / 100;
}
const adaptHp = (forceMax, t) => Math.round(forceMax * (1 + adaptBonus(t)));
const rageMax = (forceMax, t) => Math.round(forceMax * (1 + adaptBonus(t) + RAGE_BONUS));
// PACE_T: the clock aimed at (one orb from 75.8 s; the margin covers the burn going quicker than PACE_BURN hp/s says and the
// bot's clock against the game's). PACE_WIN: the bot's window (the game's 8 s, less the look's lag - so it counts less).
const PACE_T = 80, PACE_WIN = 7.5, PACE_KEEP = 4.5, PACE_KEEP_SLOW = 5.5, PACE_KEEP_FLOOR = 6.5, PACE_RETRY = 1.0, PACE_LOSS = 0.15, PACE_FLOOR = 0.81,
  PACE_HARD = 0.70, PACE_BURN = 2500, PACE_DT = 1.0;
// One look. st: kept by the caller per fight; o: { id, hp, maxHp, stage (primusStage), immune (the game's damage immunity),
// inReach (the basic attack reaches it), now (ms) }. Returns { hold (no skills; the basic attack only when atk), atk, clock,
// why ('hold' | 'time' | 'floor' | 'phase'), lost, allowed, relAt, note (a line worth logging: 'start' | 'tick' | 'burn' |
// 'over' | null) }.
function primusPace(st, o) {
  const now = o.now;
  if (st.id !== o.id) Object.assign(st, { id: o.id, clock: 0, lastT: null, lastHp: null, dropAt: null, atkAt: null, forceMax: o.maxHp || 0,
    released: null, started: false, tickAt: 0, overSaid: false, maxSaid: false });
  // Iteration 50: not once the burn is on or Primus is immune - run-054's change (Gold Rain at 1 hp, then the orbs) still read
  // as Force while the Adaptation raised its max to 35149, so forceMax became that and "phase 1 over" said 40421 (35149 + 15%)
  // for the 35149 the game gave: the model was right (30564 x 1.15 at >= 75.8 s), the line's base was not.
  if (o.stage === 0 && !st.released && !o.immune && o.maxHp > st.forceMax) st.forceMax = o.maxHp;
  if (st.lastHp != null && o.hp < st.lastHp - 0.5) st.dropAt = now;
  st.lastHp = o.hp;
  if (st.lastT != null && o.stage === 0 && !o.immune && st.dropAt != null && now - st.dropAt < PACE_WIN * 1000)
    st.clock += Math.min(PACE_DT, Math.max(0, (now - st.lastT) / 1000));
  st.lastT = now;
  const out = { hold: false, atk: true, clock: st.clock, why: null, lost: 0, allowed: 0, relAt: 0, note: null };
  if (o.stage !== 0) {
    if (!st.released) st.released = { why: 'phase', clock: st.clock };
    out.why = 'phase';
    if (!st.overSaid) { st.overSaid = true; out.note = 'over'; }
    return out;
  }
  if (st.released) { out.why = st.released.why; return out; }
  const max = st.forceMax || o.maxHp || 1, frac = o.hp / max;
  out.lost = 1 - frac;
  out.relAt = PACE_T - max * (1 - PACE_LOSS) / PACE_BURN;
  const why = st.clock + o.hp / PACE_BURN >= PACE_T ? 'time' : frac <= PACE_HARD ? 'floor' : null;
  if (why) { st.released = { why, clock: st.clock, frac }; out.why = why; out.note = 'burn'; return out; }
  out.hold = true; out.why = 'hold';
  out.allowed = PACE_LOSS * Math.min(1, st.clock / Math.max(1, out.relAt));
  const quiet = st.dropAt == null ? Infinity : (now - st.dropAt) / 1000;
  const since = st.atkAt == null ? Infinity : (now - st.atkAt) / 1000;
  const onLine = frac > PACE_FLOOR && out.lost < out.allowed;
  // Over the line: the keep-alive slower; under PACE_FLOOR slower still (a lapse past the game's 8 s only pauses the clock).
  const keep = frac <= PACE_FLOOR ? PACE_KEEP_FLOOR : out.lost > out.allowed ? PACE_KEEP_SLOW : PACE_KEEP;
  out.atk = !!o.inReach && (onLine || (quiet >= keep && since >= PACE_RETRY));
  if (out.atk) st.atkAt = now;
  if (!st.started) { st.started = true; st.tickAt = st.clock; out.note = 'start'; }
  else if (st.clock - st.tickAt >= 10) { st.tickAt = st.clock; out.note = 'tick'; }
  return out;
}
const kHp = n => `${(n / 1000).toFixed(1)}k`;
// The line for a pace note (what fight() logs).
function paceLine(p, st, o) {
  const max = st.forceMax || o.maxHp || 0, pct = Math.round(100 * o.hp / Math.max(1, max));
  const want = Math.max(PACE_T, st.clock), exp = adaptHp(max, want);
  if (p.note === 'start') return `primus pace: phase 1 - holding the skills, the basic attack paced (a hit when ${PACE_KEEP} s pass with no damage, so its combat clock runs; <= ${Math.round(PACE_LOSS * 100)}% of it by ~${Math.round(p.relAt)} s), the burn when the clock + the burn reach ${PACE_T} s (phase 2 hp ~${kHp(exp)} expected, ${kHp(adaptHp(max, 24))} at run-051's 24 s)`;
  if (p.note === 'tick') return `primus pace: phase 1 at ${Math.round(st.clock)} s, Primus ${pct}%, holding big skills until ~${Math.round(Math.max(st.clock, PACE_T - o.hp / PACE_BURN))} s (phase 2 hp ~${kHp(exp)} expected; ${kHp(adaptHp(max, st.clock + o.hp / PACE_BURN))} if burnt now)`;
  if (p.note === 'burn') return `primus pace: burn - phase 1 at ${Math.round(st.clock)} s of combat time, Primus ${pct}% (${p.why === 'time' ? `the clock + ~${Math.round(o.hp / PACE_BURN)} s of burn reach ${PACE_T} s` : `down to ${Math.round(PACE_HARD * 100)}% - no hold into the Jump Attack's range`}); phase 2 hp ~${kHp(adaptHp(max, st.clock + o.hp / PACE_BURN))} expected`;
  if (p.note === 'over') return `primus pace: phase 1 over at ${st.clock.toFixed(1)} s of combat time by the bot's clock -> phase 2 hp ~${adaptHp(max, st.clock)} expected (+${Math.round(adaptBonus(st.clock) * 100)}%; the game logs its own clock in Player.log "[Adaptation] Combat Time"), phase 3 max ~${kHp(rageMax(max, st.clock))}`;
  return null;
}
// Doom's meteors (Adapt < 50%) burst into rings of fireballs (Ai_..._Doom_Meteor_SubFireball). Iteration 45 read run-051's one
// hit as 7.9 and took them out of the "shot incoming" dash above 20% hp (doomShotWeak); run-052 showed them doing ~300 each
// (7.9 was the 8 hp the hero had left) - iteration 46 withdrew that rule and reads their paths (doomRisk / doomSpot below).
const DOOM_SHOT = /BossPrimusAeron_Adapt_Doom_Meteor_SubFireball/;
// ----- end of iteration 45's pure part

// ----- iteration 46: Primus's Doom fireballs, its Arbalest's bolts, Precision Shot in Adapt (run-052's death); pure -----
// run-052 died in Doom: 315 and 299 from Doom's meteor fireballs (Ai_..._Doom_Meteor_SubFireball) with "no red under the hero",
// the burn they leave (Se_Elm_Fire, 8.5 a tick), and a last 111 (all it had left). A fireball does ~300, a quarter of the
// hero's health - run-051's "7.9" was the 8 hp it had left (the hit is cut to what remains), so iteration 45's rule (no dash
// for them above 20%) is gone. Decompiled (history/it35/dec): Se_..._Adapt_Doom_Ongoing drops 4 x 2 meteors at random pathable
// places (0.25 s apart, 3 s between pairs; each a 5 m strike, ~2 s fuse, a 3 s stun); each, when it lands, sends DOOM_RINGS rings
// DOOM_GAP s apart of n fireballs (RoundToInt(subMeteorCount x Lerp(0.7, 1, difficulty)): 18 in run-052 - 144 listed = 2 x 4 x
// 18) from its centre, fireball i of ring w at the world yaw 360 / n x (i + w / 2): rings 1 and 3 on the spokes at k x 20 deg,
// 2 and 4 on those at 10 + k x 20 - a spoke every 10 deg, each flown by two fireballs 0.35 s apart. StandardProjectile's
// defaults (5 m/s) - run-052's three hits came 16.4 / 20.7 / 21.9 m from their meteor 3.3-4.4 s after it landed (~5 m/s), each
// within 0.30-0.43 m of a spoke (the bearings 171.5, 160.8, 329.2 deg from the meteor), the hit at 0.44-0.54 m from the
// fireball's centre. So the fireballs' paths are known before they fly: between two spokes, r m from a centre, the nearest
// path passes r x sin(5 deg) = 0.087 r off - 1.3 m at 15 m, 0.4 m at 5 m. The place to wait Doom out is far from the meteors,
// between their spokes, standing still while the rings pass (doomSpot); the mod's readers 46 give each meteor and its rings
// (/threats `doom`), else the meteors' strikes are remembered (doomNote) with run-052's numbers.
const DOOM_METEOR = /BossPrimusAeron_Adapt_Doom_Meteor$/;
const DOOM_N = 18, DOOM_RINGS = 4, DOOM_GAP = 0.175, DOOM_V = 5, DOOM_REACH = 30, DOOM_R = 0.25, DOOM_BODY = 0.3, DOOM_MARGIN = 0.45,
  DOOM_H = 4, DOOM_W = 12, DOOM_KEEP = 8, DOOM_SPOT_R = 7, DOOM_STICK = 1.5, DOOM_WALK_STEP = 0.5, DOOM_PASS = 0.15, DOOM_FADE = 0.6, DOOM_FINE = 0.5, DOOM_STOP = 0.2;
const doomLife = m => m.reach / m.v + m.rings * m.gap + 0.5;
const yawDeg = (dx, dz) => Math.atan2(dx, dz) * 180 / Math.PI;   // the game's yaw: 0 along +z, 90 along +x
// How far (deg) a bearing is from the nearest spoke of a set (phase, phase + step, ...).
function spokeOff(brg, phase, step) { const o = (((brg - phase) % step) + step) % step; return Math.min(o, step - o); }
// The meteors now: st kept by the caller ({ seen: Map, list, from }); th: /threats (its `doom` when the mod has readers 46), raw:
// its areas (the meteors' strikes otherwise). Each: { c, landAt (ms), n, step, half, rings, gap, v, reach, hit (a fireball's
// reach into the hero: its radius + DOOM_BODY), strike }. Returns st.list.
function doomNote(st, th, raw, nowMs) {
  if (th && Array.isArray(th.doom)) {
    st.from = 'mod';
    st.list = th.doom.filter(m => m && m.centre && typeof m.left === 'number').map(m => {
      const n = m.n > 0 ? m.n : DOOM_N, step = m.step > 0 ? m.step : 360 / n;
      return { c: { x: m.centre.x, z: m.centre.z }, landAt: nowMs + m.left * 1000, n, step, half: m.half != null ? m.half : step / 2,
        rings: m.rings > 0 ? m.rings : DOOM_RINGS, gap: m.ringGap > 0 ? m.ringGap : DOOM_GAP, v: m.speed > 0 ? m.speed : DOOM_V,
        reach: m.reach > 0 ? m.reach : DOOM_REACH, hit: (m.radius > 0 ? m.radius : DOOM_R) + DOOM_BODY, strike: m.strike || 5 };
    });
    return st.list;
  }
  st.from = 'strikes';
  for (const a of raw || []) {
    if (!a || !isStrike(a) || !DOOM_METEOR.test(a.type || '') || !a.centre || !(a.left >= 0)) continue;
    const key = Math.round(a.centre.x * 2) + ':' + Math.round(a.centre.z * 2);
    const m = st.seen.get(key) || { c: { x: a.centre.x, z: a.centre.z }, n: DOOM_N, step: 360 / DOOM_N, half: 180 / DOOM_N, rings: DOOM_RINGS,
      gap: DOOM_GAP, v: DOOM_V, reach: DOOM_REACH, hit: DOOM_R + DOOM_BODY, strike: a.radius || 5 };
    m.landAt = nowMs + a.left * 1000;
    st.seen.set(key, m);
  }
  for (const [k, m] of st.seen) if (nowMs - m.landAt > doomLife(m) * 1000) st.seen.delete(k);
  st.list = [...st.seen.values()];
  return st.list;
}
// Whether a fireball in flight is one of a known meteor's (its line runs back to the centre and lies on the model's spokes).
function doomOwn(s, meteors) {
  for (const m of meteors || []) {
    const ax = m.c.x - s.position.x, az = m.c.z - s.position.z;
    if (ax * s.heading.x + az * s.heading.z > 0.5) continue;   // the centre is not behind it
    if (Math.abs(ax * s.heading.z - az * s.heading.x) > 1.2) continue;
    const brg = yawDeg(s.heading.x, s.heading.z);
    if (Math.min(spokeOff(brg, 0, m.step), spokeOff(brg, m.half, m.step)) <= 1.5) return m;
  }
  return null;
}
// The Doom fireballs among the shots, each with its meteor (own) or null.
const doomShots = (shots, meteors) => (shots || []).filter(s => s && DOOM_SHOT.test(s.type || '') && s.position && s.heading).map(s => ({ s, own: doomOwn(s, meteors) }));
// How much a point risks from Doom's fireballs in the h s from tMs: each ring of each meteor still to pass it whose spoke comes
// within the fireball's reach + DOOM_MARGIN of it, and each fireball in flight not on a known meteor's spokes (its line; read
// at nowMs, the look's time) - DOOM_W for a sure hit now, less the later and the farther off the path. 0 = off every path. Pure.
function doomRisk(p, meteors, dshots, tMs, h = DOOM_H, nowMs = tMs) {
  let risk = 0;
  const add = (lat, hit, dt) => { const wide = hit + DOOM_MARGIN; if (lat < wide) risk += DOOM_W * (1 - DOOM_FADE * Math.max(0, dt) / h) * (lat <= hit ? 1 : (wide - lat) / DOOM_MARGIN); };
  for (const m of meteors || []) {
    const dx = p.x - m.c.x, dz = p.z - m.c.z, r = Math.hypot(dx, dz);
    if (r > m.reach + m.hit) continue;
    const tl = (tMs - m.landAt) / 1000, brg = yawDeg(dx, dz);
    for (let k = 0; k < m.rings; k++) {
      const tIn = (r - m.hit) / m.v + k * m.gap - tl, tOut = (r + m.hit) / m.v + k * m.gap - tl;
      if (tOut < 0 || tIn > h) continue;
      add(r * Math.sin(Math.min(90, spokeOff(brg, k % 2 ? m.half : 0, m.step)) * Math.PI / 180), m.hit, tIn);
    }
  }
  for (const { s, own } of dshots || []) {
    if (own) continue;
    const hit = (s.radius > 0 ? s.radius : DOOM_R) + DOOM_BODY, v = Math.max(0.5, s.speed || DOOM_V);
    const ax = p.x - s.position.x, az = p.z - s.position.z;
    const along = ax * s.heading.x + az * s.heading.z;
    if (along < -hit || along > (s.remaining >= 0 ? s.remaining : DOOM_REACH) + hit) continue;
    const tIn = (along - hit) / v - (tMs - nowMs) / 1000, tOut = (along + hit) / v - (tMs - nowMs) / 1000;
    if (tOut < 0 || tIn > h) continue;
    add(Math.abs(ax * s.heading.z - az * s.heading.x), hit, tIn);
  }
  return risk;
}
// Where to wait Doom out: of the cells within DOOM_SPOT_R m, the one with the least cost - its risk from the fireballs from when
// the hero gets there (walking o.v m/s) on, the risk along the straight way there as the hero passes each point, the red (the
// meteors' strikes), nearness to a meteor still to land or just landed (its rings are densest near it, its stun within 5 m),
// o.keep (Primus, invulnerable at the centre), a little for the walk and for little room. o.prev: the spot held - kept unless
// another is DOOM_STICK better. Returns { p, cost, risk, md, here (the risk where the hero stands) } or null. Pure.
function doomSpot(grid, me, meteors, dshots, areas, nowMs, o = {}) {
  if (!grid || !grid.reach) return null;
  const { origin, step, size, reach, clear } = grid;
  const v = o.v || HERO_WALK;
  const red = (areas || []).filter(a => a && !a.pool && !a.keepOut && !a.timeless);
  const costAt = (p, md, cl) => {
    const tw = md / v;
    const risk = doomRisk(p, meteors, dshots, nowMs + tw * 1000, DOOM_H, nowMs);
    let cost = risk;
    // The walk there: every DOOM_WALK_STEP m of the straight way, what passes that point while the hero does (from DOOM_PASS s
    // before to DOOM_PASS after) - crossing a path as a fireball comes is as bad as standing on it.
    const n = Math.floor(md / DOOM_WALK_STEP);
    for (let i = 1; i <= n; i++) {
      const f = i * DOOM_WALK_STEP / md;
      if (f >= 0.999) break;
      const q = { x: me.x + (p.x - me.x) * f, z: me.z + (p.z - me.z) * f };
      cost += doomRisk(q, meteors, dshots, nowMs + (f * tw - DOOM_PASS) * 1000, 2 * DOOM_PASS, nowMs);
    }
    for (const a of red) { const dp = areaDepth(p, a, 0.5); if (dp > 0) cost += 10 + 3 * dp; }
    for (const m of meteors || []) {
      if ((nowMs - m.landAt) / 1000 > 1) continue;
      const r = dist(p, m.c);
      if (r < DOOM_KEEP) cost += 0.6 * (DOOM_KEEP - r);
    }
    if (o.keep && o.keep.pos) { const kd = dist(p, o.keep.pos); if (kd < o.keep.r) cost += 0.8 * (o.keep.r - kd); }
    cost += 0.25 * md - 0.6 * Math.min(cl, 4);
    return { cost, risk };
  };
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 10 || clear[k] < 1) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, p);
    if (md > DOOM_SPOT_R) continue;
    const c = costAt(p, md, clear[k]);
    if (!best || c.cost < best.cost) best = { p, md, clear: clear[k], ...c };
  }
  if (!best) return null;
  // Within the best cell (+-DOOM_FINE m): the point farthest from the paths - near a meteor the gaps between spokes are narrower
  // than a cell (1.4 m apart at 8 m), and the middle of one is what keeps the fireballs off.
  const b0 = best;
  for (let dx = -DOOM_FINE; dx <= DOOM_FINE + 1e-9; dx += DOOM_FINE / 2) for (let dz = -DOOM_FINE; dz <= DOOM_FINE + 1e-9; dz += DOOM_FINE / 2) {
    if (!dx && !dz) continue;
    const p = { x: b0.p.x + dx, z: b0.p.z + dz }, md = dist(me, p), c = costAt(p, md, b0.clear);
    if (c.cost < best.cost - 0.05) best = { p, md, clear: b0.clear, ...c };
  }
  const here = doomRisk(me, meteors, dshots, nowMs);
  if (o.prev && o.prev.p) {
    const pk = pinCell(grid, o.prev.p);
    if (pk >= 0 && reach[pk] >= 0 && dist(me, o.prev.p) <= DOOM_SPOT_R) {
      const md = dist(me, o.prev.p), c = costAt(o.prev.p, md, clear[pk]);
      if (c.cost <= best.cost + DOOM_STICK) return { p: o.prev.p, md, clear: clear[pk], ...c, here, kept: true };
    }
  }
  return { ...best, here };
}
// The model against the fireballs seen: of a meteor's fireballs in flight (their line back to its centre), how many fly along
// its spokes (within 1.5 deg). Returns { seen, on }.
function doomCheck(m, shots) {
  let seen = 0, on = 0;
  for (const s of shots || []) {
    if (!s || !DOOM_SHOT.test(s.type || '') || !s.position || !s.heading) continue;
    const ax = m.c.x - s.position.x, az = m.c.z - s.position.z;
    if (ax * s.heading.x + az * s.heading.z > 0.5 || Math.abs(ax * s.heading.z - az * s.heading.x) > 1.2) continue;
    seen++;
    const brg = yawDeg(s.heading.x, s.heading.z);
    if (Math.min(spokeOff(brg, 0, m.step), spokeOff(brg, m.half, m.step)) <= 1.5) on++;
  }
  return { seen, on };
}

// The Arbalest (Adapt < 80%; Ai_..._Adapt_Arbalest): Primus stands (everything blocked) and shoots 4 bolts, each after aiming 1.5
// s - the aim turns every frame after where the hero will be (360 deg/s, x3 within 5 m, then smoothed over 0.1 s) - and 0.15 s
// more. The bolt is all but instant: run-052's aim box's `left` (release + flight) ran 0.06-0.1 s past its drawing's (the
// release) at ~12 m. So walking out of the line does nothing - the aim follows - and a dash too early is followed too: run-052's
// dashes 0 and 0.02 s before a release were missed, the one 0.32 s before was re-aimed and hit (503 + a knockback). The dash
// goes at the last moment: the aim box (the mod's `... (aim)`, readers 42) or its drawing (an untyped box on the same line, by
// no one) is no red to dash from until its left is within ARB_LATE / ARB_DRAW_LATE s. arbalestBoxes: those boxes (a Set).
const ARB_AIM = /Adapt_Arbalest \(aim\)/, ARB_LATE = 0.25, ARB_DRAW_LATE = 0.17;
function arbalestBoxes(areas) {
  const aims = (areas || []).filter(a => a && a.shape === 'box' && ARB_AIM.test(a.type || '') && a.facing && a.centre);
  const set = new Set(aims);
  if (!aims.length) return set;
  for (const a of areas) {
    if (!a || a.shape !== 'box' || a.type || (a.by && a.by !== '?') || !a.facing || !a.centre || set.has(a)) continue;
    if (aims.some(b => Math.abs(a.facing.x * b.facing.x + a.facing.z * b.facing.z) > 0.97 &&
      Math.abs((a.centre.x - b.centre.x) * b.facing.z - (a.centre.z - b.centre.z) * b.facing.x) < 1.5)) set.add(a);
  }
  return set;
}
// Too early to dash from one of them.
const arbalestEarly = (a, set) => set.has(a) && a.left > (ARB_AIM.test(a.type || '') ? ARB_LATE : ARB_DRAW_LATE);

// Precision Shot at Primus in its Adapt phase (run-052 12:06:33.7: 0.95 s charged at 11.4 m right after the Arbalest, the dash
// spent on its last bolt; the next Adapt Atk's wind-up was first seen with 0.1 s left - 434 and its chain). The Adapt Atk comes
// every ~3 s (run-050: 15.1, 18.4, 21.4, 24.65, 27.4), at once after Primus's dash, and when the Arbalest ends; it gives ~1 s of
// warning and its 3 m take ~0.7 s to walk out of. So a charge may last until ADAPT_SPARE s past when the next one is due
// (ADAPT_EVERY after the last one was first seen; less with no dash charge); none while one is listed, while the Arbalest aims
// (its bolt), or before the phase's first one is seen. st kept by the caller; o: { now, casting (an Adapt Atk wind-up or bolt
// listed now), primusAt, arbalest (its aim listed now) }. adaptCap: the seconds a charge may last now (0: none).
const ADAPT_EVERY = 2.7, ADAPT_SPARE = 0.3, ADAPT_SPARE_NODASH = 0.15, PRIMUS_JUMP = 3;
function adaptNote(st, o) {
  if (o.casting) { if (!st.casting) st.lastAt = o.now; st.casting = true; } else st.casting = false;
  if (st.at && o.primusAt && dist(st.at, o.primusAt) > PRIMUS_JUMP && o.now - st.atT < 700) st.dashAt = o.now;
  if (o.primusAt) { st.at = o.primusAt; st.atT = o.now; }
  if (o.arbalest) st.arbAt = o.now;
  st.arbalest = !!o.arbalest;
}
function adaptCap(st, now, dashReady) {
  if (st.casting || st.arbalest || st.lastAt == null) return 0;
  let due = st.lastAt + ADAPT_EVERY * 1000;
  if (st.dashAt && st.dashAt > st.lastAt) due = Math.min(due, st.dashAt);
  if (st.arbAt && st.arbAt > st.lastAt) due = Math.min(due, st.arbAt);
  return Math.max(0, Math.round(((due - now) / 1000 + (dashReady ? ADAPT_SPARE : ADAPT_SPARE_NODASH)) * 100) / 100);
}
// ----- end of iteration 46's pure part

// Iteration 22 (run-032's death): Infernus's Stomp. Decompiled (history/it22/Ai_Mon_LavaLand_BossInfernus_Stomp.cs): after
// the stomp, waves of Stomp_Eruption strikes (InstantDamageInstance, r 1.6) go out along a pattern's rays from where Infernus
// stood - wave w at startDistance + stepDistance x w on every ray, a wave every waveInterval, up to maxSpawnCount waves; a ray
// ends where the ground is off by 2 m. Each eruption hits an entity once per 1.5 s (hitCooldownTime) and throws it straight
// out from Infernus (the damage's direction is the eruption's facing: LookRotation(eruption - Infernus)). Measured (probe
// 07:14:05.29-05.58): 5 rays 15 deg apart aimed at the hero (-55..+5 deg, the hero at -24), rings at 3, 5.5, 8, 10.5, 13 and
// 15.5 m, each wave listed ~0.045 s after the last, each landing ~0.7 s after it shows; the throw 3.5 m (1.29,2.36 ->
// 3.04,-0.70, straight out). /threats lists a wave only once it exists: at 05.23 three rings showed, the dash went 3.6 m out
// along the middle ray to (6.2, 2.4) - where ring 4 came 0.06 s later; from there a dash 5 m across the rays landed in a
// ring-3 eruption 0.03 s before it went off (128), the throw put the hero on the lava (52 + the burn relit), dead in 1.8 s.
// So (stompFan): each ray seen is carried out to FAN_RINGS rings - the waves not listed yet, as predicted strikes landing
// FAN_DT per ring after the ray's last one (a ray with two rings listed at least, 0.3 m from them) - and every eruption, seen or predicted, is marked with `knockFrom` (Infernus's
// spot) for plan()'s throw check (knockLava). Strikes not on the rings round Infernus's position (it moved since) are left
// alone. Pure (tests/iter22.test.mjs).
const STOMP_ERUPT = /BossInfernus_Stomp_Eruption/;
const FAN_START = 3, FAN_STEP = 2.5, FAN_RINGS = 7, FAN_DT = 0.05, KNOCK = 3.5, KNOCK_W = 15;
function stompFan(areas, origin) {
  if (!origin) return [];
  const rays = [];
  for (const a of areas) {
    if (!isStrike(a) || !a.centre || a.predicted || !STOMP_ERUPT.test(a.type || '')) continue;
    const dx = a.centre.x - origin.x, dz = a.centre.z - origin.z, d = Math.hypot(dx, dz);
    if (d < 1) continue;
    const ring = Math.round((d - FAN_START) / FAN_STEP);
    if (ring < 0 || Math.abs(FAN_START + ring * FAN_STEP - d) > 0.3) continue;
    a.knockFrom = { x: origin.x, z: origin.z };
    const ang = Math.atan2(dz, dx);
    let r = rays.find(q => Math.abs(Math.atan2(Math.sin(ang - q.ang), Math.cos(ang - q.ang))) < 0.07);
    if (!r) rays.push(r = { ang, ux: dx / d, uz: dz / d, top: -1, last: null, n: 0 });
    r.n++;
    if (ring > r.top) { r.top = ring; r.last = a; }
  }
  const out = [];
  for (const r of rays) for (let k = r.top + 1; r.n >= 2 && k < FAN_RINGS; k++) {
    const d = FAN_START + FAN_STEP * k;
    out.push({ shape: 'strike', centre: { x: origin.x + r.ux * d, z: origin.z + r.uz * d }, radius: r.last.radius, inner: 0, angle: 360,
      fill: 0, left: Math.round(((r.last.left || 0) + FAN_DT * (k - r.top)) * 100) / 100, by: r.last.by, type: (r.last.type || '') + ' (predicted)',
      predicted: true, knockFrom: { x: origin.x, z: origin.z } });
  }
  return out;
}
// Standing at p inside an eruption (areas with knockFrom), the throw (KNOCK m straight out from Infernus) ends on the lava or
// crosses it (grid `hazard` 'L'): 1, else 0. Off the grid counts as nothing. Pure.
function knockLava(grid, p, areas) {
  if (!grid || typeof grid.hazard !== 'string') return 0;
  for (const a of areas) {
    if (!a.knockFrom || areaDepth(p, a) <= 0) continue;
    const dx = p.x - a.knockFrom.x, dz = p.z - a.knockFrom.z, d = Math.hypot(dx, dz) || 1;
    for (let s = 0.5; s <= KNOCK + 0.01; s += 0.5) {
      const i = Math.round((p.x + dx / d * s - grid.origin.x) / grid.step), j = Math.round((p.z + dz / d * s - grid.origin.z) / grid.step);
      if (i < 0 || j < 0 || i >= grid.size || j >= grid.size) continue;
      if (grid.hazard[j * grid.size + i] === 'L') return 1;
    }
  }
  return 0;
}

// Burning ground. In LavaLand the fire burn (Se_Elm_Fire) took more than everything else together:
// run-006's zone 1, 1050 of ~1480 hp taken - 827 of it Se_Elm_Fire/Fire Elemental - and most of it
// after "fight: clear", while looting: 181, 197, then 638 -> 110 on the way to Infernus, who then
// finished a hero at 110/696. Decompiled (history/it7): a Fire Elemental's Explosion (a strike,
// radius ~3) leaves a pool, Ai_Mon_LavaLand_FireElemental_ExplosionSub, that burns whoever stands in
// it every 0.5 s for its existTime - after its caster is dead too; the Fire Elemental's self-destruct
// and Magmadon's charge (Ai_Mon_LavaLAnd_Magmadon_Charge_Magma, a subclass) leave the same. Every fire
// hit adds a stack of the burn or restarts its timer (Actor.cs), and the burn ticks 4 times a second
// (3.5 x a level scaling x 1.3 per extra stack; ~6 a tick at lvl 8) until it lapses a few seconds
// after the last fire hit (StackedStatusEffect decay, x0.7 on heroes). So a walk through the pools
// keeps the hero burning the whole loot; standing clear of them lets it lapse. /threats does not list
// the pools (they are neither telegraphs nor damage instances); they are known here from:
//   - the Explosion strike: a pool where it lands (its radius, at least 2.5 m), for POOL_LIFE after;
//   - a hit by a pool (/damage `from` is its centre, `distance` how far the hero stood): its radius
//     at least that, kept POOL_AFTER_HIT past the last hit (POOL_FIRST_HIT for one first met so);
//   - a `zone` area, if the mod lists them (proposals/iter-7-mod.md): real radius and time left.
// existTime and the pool's radius are prefab values the code does not show, so POOL_LIFE is a
// guess; a pool that outlives it is learned again from its first hit.
const POOL_HIT = /FireElemental_ExplosionSub|Magmadon_Charge_Magma/i;
const POOL_STRIKE = /FireElemental_Explosion$/;
const POOL_LIFE = 6000, POOL_AFTER_HIT = 1500, POOL_FIRST_HIT = 3000;
// Fixed fire (iter-10): run-012 died in zone 0's start room, 24 s in - the hero broke a deposit next to
// the room's campfire (Forest_Fireplace, /damage by; no caster; not in /threats, the ground under it
// walkable, not a grid hazard), caught fire 0.46 m from it, and waitOutFire stood still there 8.2 s
// waiting for the burn to lapse while the fire kept lighting it again: 199 -> 0 (146 burn, 80 fire).
// So a fire hit by something that is nobody's (caster null, not over time, not lava - its from is the
// thing's position) marks a fixed source: FIXED_R m at least round it, for the rest of the room -
// walked out of and kept off like a pool, never waited beside.
const FIXED_R = 3, FIXED_LIFE = 600000;
const fixedSource = h => !!h && !!h.from && !h.overTime && !h.caster && !/^Se_/.test(h.by || '') && !/^LavaLand_Lava/.test(h.by || '') && !POOL_HIT.test(h.by || '') && /Fire|Flame|Burn|Ember/i.test(h.by || '');
// The Ink boss room's damaging ground (iter-11; history/it11/Ink_BossRoomDamageGround.cs): an Actor that,
// once spawned, hurts every hero within `radius` of it by dmgMaxHealthRatio of max health every
// `interval` (run-013: 52 = 4% of 1308, ~0.5 s apart, 11 ticks = 529 of the hero's last 1011 hp) until the
// room is clear. Its hits come from its own position (/damage `from`), so it is a fixed pool: at least
// GROUND_R round it, or the hero's distance + 1. Also read directly (readGrounds: position, radius) and
// passed in as `grounds`.
const GROUND_HIT = /^Ink_BossRoomDamageGround$/;
const GROUND_R = 4;
function notePools(pools, { hits = [], areas = [], grounds = [], now }) {
  // Iteration 43: a pool whose end is known (endBy - the ground's, groundEnds) is not kept past it by later hits or listings.
  const upsert = (centre, radius, born, until) => {
    const p = pools.find(q => dist(q.centre, centre) < 1.5);
    if (p) { p.radius = Math.max(p.radius, radius); p.until = Math.max(p.until, until); p.born = Math.min(p.born, born); if (p.endBy != null) p.until = Math.min(p.until, p.endBy); }
    else pools.push({ centre: { x: centre.x, z: centre.z }, radius, born, until });
  };
  for (const a of areas) if (a && a.centre && a.shape === 'zone' && GROUND_HIT.test(a.type || '')) {
    upsert(a.centre, a.radius, now, now + (a.left > 0 ? a.left * 1000 : POOL_LIFE));
    const p = pools.find(q => dist(q.centre, a.centre) < 1.5);
    if (p) { p.fixed = true; p.by = 'Ink_BossRoomDamageGround'; p.listed = true; }
    // proposals/iter-43-mod.md: once the room is clear the mod lists what is left of its last 3 s - its end.
    if (p && a.left >= 0 && a.left < 90) { const end = now + a.left * 1000 + 300; if (!(p.endBy <= end)) p.endBy = end; p.until = Math.min(p.until, p.endBy); }
  }
  const fix = (centre, by) => { const p = pools.find(q => dist(q.centre, centre) < 1.5); if (p) { p.fixed = true; p.by = by; } };
  for (const h of hits) if (fixedSource(h)) {
    upsert(h.from, Math.max(FIXED_R, (h.distance || 0) + 1.5), now, now + FIXED_LIFE);
    fix(h.from, h.by);
  }
  for (const h of hits) if (h && h.from && GROUND_HIT.test(h.by || '')) {
    upsert(h.from, Math.max(GROUND_R, (h.distance || 0) + 1), now, now + FIXED_LIFE);
    fix(h.from, h.by);
  }
  for (const g of grounds) if (g && g.centre && g.radius > 0) {
    upsert(g.centre, g.radius + 0.5, now, now + 3000);
    fix(g.centre, 'Ink_BossRoomDamageGround');
  }
  for (const a of areas) {
    if (!a || !a.centre) continue;
    if (a.shape === 'zone' && !MOVING_ZONE.test(a.type || '')) upsert(a.centre, a.radius, now, now + (a.left > 0 ? a.left * 1000 : POOL_LIFE));
    else if (isStrike(a) && POOL_STRIKE.test(a.type || '')) {
      const lands = now + Math.max(0, a.left || 0) * 1000;
      upsert(a.centre, Math.max(2.5, a.radius), lands, lands + POOL_LIFE);
    }
  }
  for (const h of hits) {
    if (!h || !h.from || !POOL_HIT.test(h.by || '')) continue;
    const known = pools.some(q => dist(q.centre, h.from) < 1.5);
    upsert(h.from, Math.max(2.5, (h.distance || 0) + 0.5), now, now + (known ? POOL_AFTER_HIT : POOL_FIRST_HIT));
  }
  for (let i = pools.length - 1; i >= 0; i--) if (pools[i].until <= now) pools.splice(i, 1);
}
// Iteration 43 (the user: "the bot ignores the damaging zone in the centre on Lotus Plateau"; runs 046-051). The ground is one
// circle of `radius` 10.5 round (-37.9, 48.6), the Ink boss arena's centre - no growth, no second one; its OverlapCircle takes
// the hero's body too, so the pool kept at radius + 0.5 is its reach. What the bot got wrong is its *end*: Spawn() hooks
// Room.onRoomClear to a Routine that stops the effect, waits 3 s and destroys it - `_spawnGroundEnable` is never reset, so it
// hurts (4% of max hp every 0.5 s) for 3 s after the clear, then is gone. The bot kept it 600 s (FIXED_LIFE, from its hits):
// the loot after the kill walked into it while it still ticked (the soul and the drops lie in it: run-046 ~140, run-047 ~256,
// run-049 ~200, run-051 ~200) and then stood and backed out of a ground no longer there ("fire: a fixed fire
// (Ink_BossRoomDamageGround) (11.3m, 600.0s more) under the hero - waiting", 4-9 s a kill).
// groundEnds: the ground's pools end at `at` (the clear + GROUND_GRACE), kept so (endBy) whatever hits or listings come after.
// groundGone: one /threats listed before (p.listed) and not listed now with the hero in the read's reach - destroyed: ended now.
// Both pure; return how many pools they ended.
const GROUND_GRACE = 3500;
const isGround = p => !!p && GROUND_HIT.test(p.by || '');
function groundEnds(pools, at) {
  let n = 0;
  for (const p of pools) if (isGround(p) && !(p.endBy <= at)) { p.endBy = at; p.until = Math.min(p.until, at); n++; }
  return n;
}
function groundGone(pools, areas, me, now, reach = 25) {
  if (!Array.isArray(areas) || !me) return 0;
  let n = 0;
  for (const p of pools) {
    if (!isGround(p) || !p.listed || p.until <= now || dist(me, p.centre) > reach) continue;
    if (areas.some(a => a && a.centre && a.shape === 'zone' && GROUND_HIT.test(a.type || '') && dist(a.centre, p.centre) < 1.5)) continue;
    p.endBy = now; p.until = now; n++;
  }
  return n;
}
// Iteration 43: after the boss, before the clear (fight()'s quiet look): hold off the ground - { p, out: where to step to (radius
// + 1.5 m out along the line from its centre) or null (outside: stand) }, or null (no hold: not the boss room, the boss not
// seen, someone alive, no live ground with no end known, or held GROUND_HOLD_MAX already). Pure.
const GROUND_HOLD_MAX = 12000;
function groundHold(pools, now, me, { bossSeen, bossRoom, alive, quietMs = 0 } = {}) {
  if (!bossSeen || !bossRoom || alive !== 0 || !me || quietMs > GROUND_HOLD_MAX) return null;
  const p = groundLive(pools, now);
  if (!p) return null;
  const d = dist(me, p.centre);
  if (d >= p.radius + 0.8) return { p, out: null };
  const k = (p.radius + 1.5) / Math.max(0.1, d);
  return { p, out: { x: p.centre.x + (me.x - p.centre.x) * k, z: p.centre.z + (me.z - p.centre.z) * k } };
}
// A live ground with no end known yet (the boss not down, or down and the room not clear yet): its pool, or null. Pure.
const groundLive = (pools, now) => pools.find(p => isGround(p) && p.born <= now && now < p.until && p.endBy == null) || null;
// Iteration 43: the ground under `to` or across the straight way from `me` (margin for the body): its pool, or null. Pure.
function groundOnWay(pools, now, me, to, margin = 0.8) {
  for (const p of pools) {
    if (!isGround(p) || p.born > now || now >= p.until) continue;
    if (dist(me, p.centre) < p.radius + margin || (to && segDist(p.centre, me, to) < p.radius + margin)) return p;
  }
  return null;
}
// The pools burning now, as areas for plan(): always full, never "about to land" (left 99), so
// walking and dash landings keep off them, but they never set off a dash themselves.
const poolAreas = (pools, now) => pools.filter(p => p.born <= now && now < p.until).map(p => ({
  shape: 'circle', centre: p.centre, radius: Math.round(p.radius * 10) / 10, inner: 0, angle: 360, fill: 1, left: 99, pool: true, by: 'pool', type: 'pool',
  fixed: !!p.fixed, src: p.by || null,
}));
// Iteration 21: a pool (poolAreas) the hero stands in, or that the straight walk from `me` to `to` crosses (with 0.8 m for the
// body and the walk's wobble) - the fight's straight walk-up is not taken through one. Pure (tests/iter21.test.mjs).
function poolOnWay(areas, me, to) {
  for (const a of areas) if (a.pool && a.centre && (dist(me, a.centre) < a.radius + 0.5 || segDist(a.centre, me, to) < a.radius + 0.8)) return a;
  return null;
}
// Iteration 21: out of a pool in LavaLand (waitOutFire) - run-031's Combat_0_1: the step out of an ExplosionSub pool
// (a straight /hero/move away from its centre) went onto the lava twice at the clear (17 + 17, the burn relit to 7 stacks,
// 16.8 a tick; 391 -> 80 before the next room). The nearest cell the grid reaches (<= 8 steps) with room (clear >= 2), not
// under the rising lava, off every burning pool (+0.8 m), and the straight line to it on standable cells. null: none.
// Pure (tests/iter21.test.mjs).
function poolExitCell(g, me, poolList) {
  if (!g || typeof g.walk !== 'string') return null;
  const wet = wetCells(g);
  let best = null;
  for (let k = 0; k < g.reach.length; k++) {
    if (g.reach[k] < 0 || g.reach[k] > 8 || g.clear[k] < 2 || wet(k)) continue;
    const p = gridPos(g, k), d = dist(me, p);
    if (best && d >= best.d) continue;
    if (poolList.some(q => dist(p, q.centre) < q.radius + 0.8)) continue;
    const n = Math.max(1, Math.ceil(d / (g.step * 0.5)));
    let ok = true;
    for (let t = 1; t <= n && ok; t++) { const q = gridCell(g, { x: me.x + (p.x - me.x) * t / n, z: me.z + (p.z - me.z) * t / n }); if (q < 0 || g.walk[q] !== '.') ok = false; }
    if (!ok) continue;
    best = { p, d };
  }
  return best;
}
// A pool burning now that the hero stands in, or that the straight way from `a` to `b` crosses.
function poolInWay(pools, now, a, b, margin = 0.8) {
  for (const p of pools) {
    if (p.born > now || now >= p.until) continue;
    // A fixed fire only when the hero stands at it: it never goes out, so waiting for it is no use - unless its end is known
    // (iteration 43: the Ink ground after the room's clear, endBy).
    if (dist(a, p.centre) < p.radius + margin || (b && (!p.fixed || p.endBy != null) && segDist(p.centre, a, b) < p.radius + margin)) return p;
  }
  return null;
}

// Nyx's Blackhole (zone 2's boss). run-009, the loop's first Nyx kill: the hero finished at 147/1308
// - 16 ticks of Ai_Mon_Sky_BossNyx_Blackhole in ~2 s, 51 -> 92 each and growing, from the arena's
// centre with the hero 1.4-3.7 m from it (none at 4.1-4.3 m) - and Nyx died with ~1 s to spare.
// /threats did not list it; the bot circled Nyx at 1-4 m, held the last dash ("boss close" / "on
// top of us" are soft reasons) and cast its charged shot while being pulled in (5 m in 0.4 s).
// Decompiled (history/it9/Ai_Mon_Sky_BossNyx_Blackhole.cs): Nyx flies to the room's centre
// (Sky_BossRoomCenter) over displaceDuration, turns invisible and unstoppable, then for
// blackholeDuration: every entity not displacing (a dash is a displacement) is pulled toward her,
// strength by distance and by the time since it started; every tickInterval everyone within
// tickDamageRadius (3.5 by default; overlap with the hero's body) takes tickDamageRatio of max
// health x a multiplier growing over its life; it rains Starfall waves (strikes, read already); and
// it ends with a blow of explodeDamageRatio of max health within explodeDamageRadius (4).
// Known here from, best first:
//   - /threats, if the mod lists it (proposals/iter-9-mod.md: type Ai_Mon_Sky_BossNyx_Blackhole,
//     radius = tickDamageRadius, fill 1 once it is on);
//   - a pure read of the ability (bossBlackhole): on, or coming (created, Nyx still flying in);
//   - its hits in /damage (readHits): on while they come, and while Nyx stays where they came from;
//   - Nyx standing at the arena's centre: kept away from on foot only (no dash).
// What the hero does: keeps BH_KEEP m beyond the damage radius (a ring of red plan() walks out of,
// never a dash reason by itself), dashes straight away from it - the last charge too - once within
// BH_DASH m of the damage (the pull does not act on a dash), and does not cast or attack while inside
// the ring with it on (the charged shot held it still while it was pulled in).
const BH_HIT = /BossNyx_Blackhole/;
const BH_R = 3.5, BH_KEEP = 4, BH_DASH = 1.5, BH_BODY = 0.5;
// { on, centre, dmgR, how, soft } or null. area: the mod's listing; polled: 'on' | 'coming' | null
// from the read; hit: { first, last, at } from /damage; boss: Nyx; centre: the room's centre.
function blackholeNow({ area, polled, hit, boss, centre, dmgR = BH_R, now }) {
  const bp = boss && boss.position;
  if (area) return { on: area.fill >= 1, centre: area.centre, dmgR: area.radius || dmgR, how: '/threats', left: area.left, pull: area.pull };
  if (polled === 'on') return { on: true, centre: bp || centre, dmgR, how: 'read, on' };
  if (polled === 'coming' && (centre || bp)) return { on: false, centre: centre || bp, dmgR, how: 'read, Nyx flying in' };
  if (hit && hit.at && hit.last && now - hit.last < 1500) return { on: true, centre: bp && dist(bp, hit.at) < 2 ? bp : hit.at, dmgR, how: 'its hits' };
  if (hit && hit.at && hit.first && now - hit.first < 12000 && bp && dist(bp, hit.at) < 1.5) return { on: true, centre: bp, dmgR, how: 'its hits, Nyx still there' };
  if (centre && bp && dist(bp, centre) < 1) return { on: false, centre: bp, dmgR, how: 'Nyx at the arena centre', soft: true };
  return null;
}
// The ring plan() keeps out of: full, never "about to land" (left 99) - walked out of, not dashed for.
const blackholeArea = b => ({ shape: 'circle', centre: b.centre, radius: Math.round((b.dmgR + BH_KEEP) * 10) / 10, inner: 0, angle: 360,
  fill: 1, left: 99, keepOut: true, by: 'Nyx', type: 'blackhole' + (b.on ? '' : b.soft ? ' (Nyx at the centre)' : ' (coming)') });
// A dash straight away from a point: the reachable cell 3.5-5.5 m off that is farthest from it,
// with room around it counting a little.
// avoid: red areas a landing inside costs (the Blackhole's Starfall strikes), keepOut rings aside.
function awayCell(grid, me, from, avoid = []) {
  const { origin, step, size, reach, clear } = grid;
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, p);
    if (md < 3.5 || md > 5.5) continue;
    let red = 0;
    for (const a of avoid) if (!a.keepOut && areaDepth(p, a, 0.4) > 0) red += 3;
    const score = dist(p, from) + 0.3 * Math.min(clear[k], 5) - (clear[k] <= 1 ? 3 : 0) - red;
    if (!best || score > best.score) best = { p, md, clear: clear[k], score, red };
  }
  return best;
}

// The Blackhole once it is coming or on (iteration 12). run-015 died in it with Nyx at 14%: the hero
// dashed out at 5.4 m (outside the 3.5 m damage), then - circling at fighting range, casting Q in the
// ring - drifted back to 4.9 m, dashed again at 5.2 m with its last charge, and with none left was
// dragged from 9.7 m to 1.2 m in 2.4 s: nine ticks of 95-108 in ~0.9 s, 755 -> 0. The boss trace
// (radial / tangential speed about the centre, runs 014 and 015): the hero kept ~5 m/s *sideways* the
// whole time (plan() circles; its `fit` wants ~7 m from Nyx, who is at the centre), so it never walked
// out; right after each dash it walked straight back in (-8..-12 m/s); a cast held it while the pull
// took it 5-6 m in 0.4-0.5 s (run-014's E at 8.3 m -> 2.2 m). Decompiled: the pull
// (attractStrengthByDist over distanceBounds 4..14 m x attractStrengthMulOverLifetime) moves everyone
// not displacing toward her each frame; the hero walks ~5 m/s. So from the moment it is known:
//   - no casts, no attacks, no circling: walk straight away from it (bhWalk) until past its pull
//     (`pull` from /threats = distanceBounds.y, 14 m) + 1, then stand;
//   - the dash charges are kept for it - no soft dash, a hard one (red about to land) only with both
//     charges and landing farther out;
//   - a dash out (awayCell): coming (Nyx still flying in, ~1 s) within BH_PRE m with both charges - the
//     charge is back ~3.4 s later; on and losing ground on foot (the distance shrinking > BH_LOSING m/s
//     while walking out, or the read pull stronger than the walk) within BH_LOSE_AT m with both
//     charges, or within BH_LOSE_AT - 2 m with one; and always within BH_BODY + BH_EDGE of the damage.
const BH_PRE = 10, BH_LOSE_AT = 8.5, BH_LOSING = 0.8, BH_EDGE = 1.2, BH_WALK = 5;
// The reachable cell to walk to: the most ground gained away from the centre per metre walked (a wall
// straight out sends it round), off the red about to land; its first step as `way`. Pure.
function bhWalk(grid, me, centre, avoid = []) {
  const { origin, step, size, reach, clear } = grid;
  const d0 = dist(me, centre);
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 1 || reach[k] > 8 || clear[k] < 1) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const gain = dist(p, centre) - d0;
    if (gain <= 0) continue;
    let red = 0;
    for (const a of avoid) if (!a.keepOut && areaDepth(p, a, 0.4) > 0) red += 1.5 + 0.5 * a.fill;
    // Iteration 18: and the strikes the way there crosses as they land (crossRed) - run-026's walk out of the
    // Blackhole went back into a Starfall with 0.07 s left (132); only where the walk ended was weighed.
    red += crossRed(me, p, avoid) / 6;
    const score = gain / Math.max(1, reach[k] * step) + 0.03 * gain + 0.05 * Math.min(clear[k], 4) - red;
    if (!best || score > best.score) best = { k, p, gain, reach: reach[k], score };
  }
  if (!best) return null;
  return { ...best, way: stepBack(grid, best.k) };
}
// The first steps of the grid's path to cell k: back down the reach gradient to within 3 cells.
function stepBack(grid, k) {
  const { origin, step, size, reach } = grid;
  while (reach[k] > 3) {
    const i = k % size, j = Math.floor(k / size);
    let next = -1;
    for (let dj = -1; dj <= 1 && next < 0; dj++) for (let di = -1; di <= 1; di++) {
      const ni = i + di, nj = j + dj;
      if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
      const n = nj * size + ni;
      if (reach[n] === reach[k] - 1) { next = n; break; }
    }
    if (next < 0) break;
    k = next;
  }
  return { k, p: { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step } };
}
// What to do this tick with the Blackhole coming or on. Pure.
//   b        blackholeNow's { on, centre, dmgR, pull }
//   vr       the hero's speed away from the centre over the last ~0.3 s walking (null: not known)
//   pullAt   (d) -> the pull in m/s now, from the read curves (null: not read)
//   charges  dash charges (undefined: not known), canDash: one is ready
//   urgent   red about to land on the hero (areaUrgent / a shot)
// Returns { dash: cell, why } | { walk: { p, way }, why } | { hold: true, why }.
//   dodge    a strike or telegraph under the hero landing within ~1.2 s (iteration 15: bhDodge; not a dash reason)
function blackholeMove({ grid, me, b, vr = null, pullAt = null, charges, canDash, urgent = false, dodge = false, avoid = [] }) {
  const d = dist(me, b.centre);
  const edge = b.dmgR + BH_BODY;
  const two = charges == null ? false : charges >= 2;
  const pulled = pullAt ? pullAt(d) : null;
  const losing = (vr != null && vr < -BH_LOSING) || (pulled != null && pulled > BH_WALK + 0.3);
  let why = null;
  if (canDash) {
    if (d < edge + BH_EDGE) why = `${(d - edge).toFixed(1)}m from its damage`;
    else if (!b.on && d < BH_PRE && two) why = 'coming, before it opens';
    else if (b.on && losing && (two ? d < BH_LOSE_AT : d < BH_LOSE_AT - 2)) why = `losing ground (${vr != null ? vr.toFixed(1) + ' m/s' : 'pull ' + pulled.toFixed(1) + ' m/s'})`;
    else if (urgent && two) why = 'red about to land';
  }
  if (why) {
    const cell = awayCell(grid, me, b.centre, avoid);
    if (cell && dist(cell.p, b.centre) > d + 2 && (cell.red === 0 || !urgent || d < edge + BH_EDGE)) return { dash: cell, why };
  }
  // Red about to land on the hero and no dash for it: a step off it first (bhDodge), whatever it costs in
  // ground - the pull only takes what the dodge's second gives it, a Starfall takes ~10% of max hp a blow.
  if ((urgent || dodge) && d > edge + BH_EDGE + 2) {
    const dg = bhDodge(grid, me, b.centre, avoid, pulled != null ? pulled : BH_WALK);
    if (dg) return { walk: dg, why: 'stepping off the red' };
  }
  const far = (b.pull || 14) + 1;
  if (d >= far && !urgent) return { hold: true, why: 'past its pull' };   // red about to land: walk on, off it
  const w = bhWalk(grid, me, b.centre, avoid);
  return w ? { walk: w, why: 'walking out' } : { hold: true, why: 'no way farther out' };
}
// Off a strike about to land while the Blackhole pulls (iteration 15). run-020: Nyx's Starfall rains through
// her Blackhole (13 strikes of 2.35 m at a time) - 270 + 455 taken while "walking out": bhWalk heads straight
// away from the centre and scores only where it ends, and the pull (4-8 m/s by then, the walk 5) held the
// hero still in a strike for 0.85 s (in 0.6 -> 0.4 m, 14.9 m from the centre). Radial walking fights the
// pull; sideways it does not. So: the reachable cell 1-3 m off whose walk, with the pull on it for DODGE_T s,
// ends in the least red (areaDepth, 0.4 m margin), and then the most ground away from the centre; only if
// that is less red than standing. Pure; tests/iter15.test.mjs.
const DODGE_T = 0.6, BH_RED_MARGIN = 0.9;   // the hero body (0.4) + what the pull drags it in before a strike lands (iteration 17)
function bhDodge(grid, me, centre, avoid = [], pull = BH_WALK) {
  const { origin, step, size, reach, clear } = grid;
  const red = p => { let r = 0; for (const a of avoid) if (!a.keepOut && !a.pool) r += areaDepth(p, a, BH_RED_MARGIN); return r; };
  const d0 = Math.max(0.1, dist(me, centre));
  const inward = { x: (centre.x - me.x) / d0, z: (centre.z - me.z) / d0 };
  const now = red(me);
  if (now <= 0) return null;
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 1 || reach[k] > 3 || clear[k] < 1) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, p);
    if (md < 0.9) continue;
    const u = { x: (p.x - me.x) / md, z: (p.z - me.z) / md };
    const go = Math.min(md, BH_WALK * DODGE_T);
    const end = { x: me.x + u.x * go + inward.x * pull * DODGE_T, z: me.z + u.z * go + inward.z * pull * DODGE_T };
    const r = red(end);
    const score = -3 * r + 0.1 * (dist(end, centre) - d0);
    if (!best || score > best.score) best = { k, p, red: r, score };
  }
  // Iteration 17: a step that ends clear of it always counts - run-024 was 0.15 m in (with the margin) and
  // "0.3 less" could not be met, so no dodge: the walk out, the pull, 111.6.
  if (!best || (best.red > 0 && best.red >= now - 0.3)) return null;
  return { ...best, way: { k: best.k, p: best.p } };
}

// The pull at distance d, `elapsed` s after it opened, from the read curves (linear between samples);
// null when they were not read. Pure.
function pullFrom(curves, bounds, duration, d, elapsed) {
  if (!curves || !bounds || !(bounds.y > bounds.x) || !(duration > 0)) return null;
  const at = (ys, x) => { const u = Math.max(0, Math.min(1, x)) * (ys.length - 1), i = Math.min(ys.length - 2, Math.floor(u)); return ys[i] + (ys[i + 1] - ys[i]) * (u - i); };
  return at(curves.byDist, (d - bounds.x) / (bounds.y - bounds.x)) * at(curves.overLife, elapsed / duration);
}

// The Seeker's chaser orbs (zone 1's DarkCave boss). run-010: ~25 s of its 56 s fight the hero was
// 15-26 m from the Seeker doing next to nothing (its hp 10236 -> 11771 -> 10637, 02:43:09-36). Each
// orb (Ai_Mon_DarkCave_BossSeeker_GreenChaserOrb: homing, 4 m/s) comes with a blob /threats reads from
// its drawing's bounds - 16.3-16.5 m round the orb itself (the blob's centre is the orb's position,
// no caster) - and plan() walked out of that circle: the hero walks ~5 m/s, the orb follows at 4, so
// the hero backed off 10-14 m from the boss for the orb's whole life each time (boss-trace 02:43:20-24,
// 02:43:29-33). Decompiled (history/Ai_Mon_DarkCave_BossSeeker_GreenChaserOrb*.cs): it explodes on
// touching the hero or after maxDuration (4 s) wherever it is, its explosion (an InstantDamageInstance
// - a strike when it has a delay) scaled 0.5 -> 1 by its age. So: the drawing is left out, and the
// orb is a small ring of red the walk keeps out of (never a dash reason by itself - left 99, like the
// Blackhole's ring), with a dash straight away from it when it comes within ORB_DASH.
const CHASER = /GreenChaserOrb$/;
// Iteration 13 (run-018): the orb that went its full 4 s blew up as a strike of radius 5
// (GreenChaserOrb_Explode, listed with 0.01 s left - too late for anything) 3.4 m from the hero: 256.
// The hero had dashed from 2 m to 7 m of it, then walked at 2.3-3.5 m of it for 2 s - inside a 3.5 m
// ring the blast is bigger than. So the ring is the blast's size plus a margin, and an orb near the end
// of its life (ORB_LATE s since first seen) within the blast is dashed from like one about to touch.
const ORB_BLAST = 5, ORB_KEEP = ORB_BLAST + 0.5, ORB_DASH = 2, ORB_LATE = 3.2;
// { orbs: the chaser orbs in the air, rest: the areas less the orbs' drawings, dropped: those }. Pure.
function chaserOrbs(shots, areas) {
  const orbs = shots.filter(s => s && s.position && CHASER.test(s.type || ''));
  if (!orbs.length) return { orbs, rest: areas, dropped: [] };
  const drawing = a => a.shape === 'blob' && orbs.some(o => dist(a.centre, o.position) < 2.5);
  return { orbs, rest: areas.filter(a => !drawing(a)), dropped: areas.filter(drawing) };
}
const orbArea = o => ({ shape: 'circle', centre: { x: o.position.x, z: o.position.z }, radius: ORB_KEEP, inner: 0, angle: 360,
  fill: 1, left: 99, keepOut: true, by: 'Seeker', type: 'chaser orb' });

// White Night's Cataclysm (zone 2's Ink boss, Room_Ink_Boss_0). run-013 died there: the bot took White
// Night 21186 -> 13771 in ~5 s, then her hp froze for the last 20 s - at 65% she turns invulnerable
// (Se_Mon_Ink_Boss_PhaseChange), teleports to Ink_BossTeleportPosition and casts the Cataclysm. The hero
// carried Se_Mon_Ink_BossWhiteNight_Cataclysm_NotSafe from 03:18:37.8, /threats listed nothing ("0 red
// areas"), and it kept circling her at 7-11 m, dash ready: two Cataclysm blows (301, 221, each with a
// stun and a knockback) and 11 ticks of Ink_BossRoomDamageGround (52 = 4% of max health each) -> dead.
// Decompiled (history/it11/): each of cWaveCount waves (cInterval apart, halved in rage) picks safe
// points anew (GetSafeZonePositions): one per hero, 0.65-1 x heroSafeZoneDeviation from where the hero
// stands, and more round Ink_BossRoomCenter (globalSafeZoneRange from it, not within 10 m of a hero);
// an Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone holds them (_points, _radius shrinking each wave by
// safeZoneReduceRatio, _endTime = cCastDelay + endDelay ahead) and every checkInterval gives NotSafe to
// every enemy of hers outside all the circles and takes it off those inside; then
// Ai_Mon_Ink_BossWhiteNight_Cataclysm_Instance, after its delay, hits everyone with NotSafe (dmgFactor,
// knockback from her, stun). Nothing is drawn as a telegraph; the circles are shield effects.
// So: read the circles (a pure read of the SafeZone - the host has _points - or the mod's /threats
// `safe` areas, proposals/iter-11-mod.md), pick the one quickest to reach (/nav/path lengths; off the
// damaging ground), walk into it - dash when walking will not make it in time - and stand in it until
// the blow; no circling, no soft dashes meanwhile.
const NOT_SAFE = 'Se_Mon_Ink_BossWhiteNight_Cataclysm_NotSafe';
const HERO_SPEED = 4.5;   // m/s on foot (run-013's boss trace: ~0.3 m per 70 ms tick)
// How far inside a circle to stand: the check is an overlap with the hero's body, the hero's position
// read ~0.1 s late.
const safeMargin = r => Math.min(1, Math.max(0.4, r * 0.35));
// The circle to go to: the quickest to get into (path length when known - lens[i], null for no path -
// else straight), one on the damaging ground (grounds: pool areas) only if nothing else. Pure.
function safeSpot(safe, me, grounds = [], lens = null) {
  const havePaths = Array.isArray(lens) && lens.some(l => typeof l === 'number');
  let best = null;
  safe.points.forEach((p, i) => {
    const straight = dist(me, p);
    let cost = havePaths ? (typeof lens[i] === 'number' ? lens[i] : 1e6) : straight;
    cost = Math.max(0, cost - (safe.radius - safeMargin(safe.radius)));
    const onGround = grounds.some(g => dist(p, g.centre) < g.radius + 0.5);
    if (onGround) cost += 15;
    if (!best || cost < best.cost) best = { p, i, cost, straight, onGround, len: havePaths ? lens[i] : null };
  });
  return best;
}
// What to do about it now: 'stay' (inside, with the margin), 'walk', or 'dash' (walking would not get
// in before the blow - left: seconds - or it is far; a dash covers ~5 m at once). Pure.
function safeMove(spot, radius, me, left, dashReady) {
  const d = dist(me, spot.p), need = d - (radius - safeMargin(radius));
  if (need <= 0) return { act: 'stay', d, need: 0 };
  const walkT = need / HERO_SPEED;
  if (dashReady && need > 2 && (walkT + 0.5 > left || need > 6)) return { act: 'dash', d, need, walkT };
  return { act: 'walk', d, need, walkT };
}
// The dash landing that gets nearest into the circle: a reachable cell 2.5-5.5 m away with room, at
// least 2 m nearer the circle than the hero is. Pure.
function safeDashCell(grid, me, p, radius) {
  const { origin, step, size, reach, clear } = grid;
  const inR = radius - safeMargin(radius), now = Math.max(0, dist(me, p) - inR);
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8 || clear[k] < 1) continue;
    const c = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, c);
    if (md < 2.5 || md > 5.5) continue;
    const out = Math.max(0, dist(c, p) - inR);
    const score = out + 0.05 * md - 0.1 * Math.min(clear[k], 3);
    if (!best || score < best.score) best = { p: c, md, out, clear: clear[k], score };
  }
  return best && best.out <= now - 2 ? best : null;
}

// The lava rising (iteration 15; the iter-14 mod's /nav/grid `lava`: enableTranslation, t 0 low .. 1 high,
// rising): then the cells it covers at its high ('l' in `hazard`) are no place to stand or walk across. Only
// LavaLand_Lava with enableTranslation moves (run-021's room: false).
function lavaRising(g) {
  return !!(g && g.lava && g.lava.enableTranslation && (g.lava.rising || g.lava.t > 0.7));
}
function wetCells(g) {
  const on = lavaRising(g) && typeof g.hazard === 'string';
  return k => on && g.hazard[k] === 'l';
}

//   opts.desired  the distance to hold from the centre instead of the shooting range
//   opts.keep     { pos, r }: stay out of r of pos (a shielded boss while its pillars are shot)
//   opts.bossMelee the boss's melee radius for the cells (5.5 m; iteration 33: less while Infernus breathes - it cannot swing)
//   opts.side     the way round to keep (+1 ccw / -1 cw), no flip (iteration 33: away from Infernus's facing)
//   opts.openDir  the open side (a unit vector; iteration 44's pinRead): near a wall the circling leans that way
//   opts.approach a point to walk toward instead of circling (iteration 17: the way to shootSpot's place for
//                 a pillar across the lava) - no sideways pull, the fit is the distance to it
//   opts.momentum the way the hero is walking (a unit vector; iteration 17: Infernus's meteors fall where the
//                 hero is - keep going the same way rather than turn on the spot)
//   opts.doom     p => the risk at p from Primus's Doom fireballs' paths (iteration 46: doomRisk), added to the cell's line
// Iteration 17: a walk's straight line through a timed red (a strike, a filling telegraph) that lands while
// the hero is in it counts against the cell (`cross`) - run-023's death: after a dash out of a meteor, plan()
// walked back east through two meteors about to land (0.3-0.4 s left) to a cell with no red, 2 x 79.
const POOL_CROSS = 12;
function crossRed(me, p, areas, v = 4.5) {
  const L = Math.hypot(p.x - me.x, p.z - me.z);
  if (L < 0.1) return 0;
  const ux = (p.x - me.x) / L, uz = (p.z - me.z) / L;
  let pen = 0;
  for (const a of areas) {
    // Iteration 28 (run-040's Nyx, boss-trace 08:55:03.6-05.4): ground that hurts while stood in (a pool: burning ground,
    // the Ink room's ground, Nyx's Pillar of Stars once it ticks - /threats `zone`) was left out here, so the walk to a cell
    // beyond one went straight through it: 4.8 -> 1.1 m from a Pillar's centre (r 1.8), 175 in one look. Its line
    // passing through one the hero is not in already (0.4 m for the body), to a cell outside it, costs POOL_CROSS - when
    // it comes nearer the centre than the hero stands now (along its edge or away from it is no crossing).
    if (a.pool && a.centre) {
      const sd = segDist(a.centre, me, p);
      if (areaDepth(me, a, 0) <= 0 && areaDepth(p, a) <= 0 && sd < (a.radius || 0) + 0.4 && sd < dist(me, a.centre) - 0.2) pen += POOL_CROSS;
      continue;
    }
    // Iteration 50 (run-054, Primus's Force Atk 524 at 578 hp): boxes and polygons were left out here, so a walk along one's
    // edge cost nothing - the hero dashed out of the Atk's polygon to 0.91 m past its edge, then the orbit walked it along that
    // edge to 0.39 m past it with 0.1 s left, and the blow (the body counts) hit. Now a walk whose line passes within the margin
    // of a box or polygon landing while the hero is on that stretch costs as a circle's crossing does (sampled every 0.5 m);
    // not one the hero stands in (the way out is plan()'s walkOut / sweeps) nor one the cell ends in (its own red counts it).
    if (a.shape === 'box' || a.shape === 'poly') {
      if (!a.corners || a.timeless || a.keepOut || !(a.left >= 0) || a.left > 1.6 || areaDepth(me, a, 0) > 0 || areaDepth(p, a) > 0) continue;
      const n = Math.max(2, Math.ceil(L / 0.5));
      let tIn = null, tOut = null;
      for (let i = 1; i <= n; i++) {
        const s = L * i / n;
        if (areaDepth({ x: me.x + ux * s, z: me.z + uz * s }, a) > 0) { if (tIn === null) tIn = (s - L / n) / v; tOut = s / v; }
      }
      if (tIn !== null && a.left > tIn - 0.15 && a.left < tOut + 0.2) pen += 10 + 6 * (a.fill || 0);
      continue;
    }
    if (a.timeless || a.keepOut || a.shape === 'ring' || !(a.left >= 0) || a.left > 1.6 || !a.centre) continue;
    if (areaDepth(p, a) > 0) continue;   // it ends there: the cell's own red counts it
    const R = (a.radius || 0) + 0.4;
    const ax = a.centre.x - me.x, az = a.centre.z - me.z;
    const along = ax * ux + az * uz, side = Math.abs(ax * uz - az * ux);
    if (side >= R) continue;
    const hc = Math.sqrt(R * R - side * side);
    if (along + hc <= 0 || along - hc >= L) continue;   // not on the way
    const tIn = Math.max(0, along - hc) / v, tOut = (along + hc) / v;
    if (a.left > tIn - 0.15 && a.left < tOut + 0.2) pen += 10 + 6 * (a.fill || 0);
  }
  return pen;
}
// Iteration 51: shots that set the hero on fire. The LavaLand burn (Se_Elm_Fire) cost 428-514 hp a LavaLand visit (runs 042-053),
// 1.5-3x the fire hits that lit it; Hellfire Spider's LavaAtk (history/it51: a StandardProjectile, 13.5 m/s, collision radius
// 0.25, 20 m; fire damage; a miniboss fires 3, 15 deg apart) hit 22 times in runs 030-055 (~25 each at level 5, then ~40-65 of
// burn: 25 + 38 + 25 in run-053 12:14:31). The rooms' fights keep the "shot incoming" dash for a boss or under 50% hp - so a
// burning shot gets it in any fight while a spare charge stays (burnDash), and plan()'s side-step sees it from BURN_DODGE.eta s
// and BURN_DODGE.miss m off its line (1.0 s / 0.8 m for the others), weighed BURN_DODGE.w (2.5).
const BURN_SHOT = /InfernoSpider_LavaAtk/;
const burnShot = s => !!s && !s.homing && BURN_SHOT.test(s.type || '');
const BURN_DODGE = { eta: 1.3, miss: 1.1, w: 4 };
function plan(grid, me, entities, target, range, shots, areas = [], opts = {}) {
  const { origin, step, size, reach, clear } = grid;
  const cellPos = k => ({ x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step });
  const boss = entities.find(isBossE);
  // Hazard ground about (LavaLand's lava: grid.hazardCells, `walk` '#'): iteration 14. The cells are off it
  // already (reach), but a straight walk to one can cross it, and the lava rises and falls
  // (LavaLand_Lava.enableTranslation), so its edge is not where the grid saw it a moment ago.
  // Iteration 15: with the lava rising (the iter-14 mod's `lava`), the cells it reaches at its high ('l' in
  // `hazard`) count as lava too - neither a place to go nor a way across.
  const wet = wetCells(grid);
  const hazard = typeof grid.walk === 'string' && (grid.hazardCells > 0 || (lavaRising(grid) && typeof grid.hazard === 'string' && grid.hazard.includes('l')));
  const lineOk = (a, b) => {
    const n = Math.max(1, Math.ceil(dist(a, b) / (step * 0.5)));
    for (let s = 0; s <= n; s++) {
      const i = Math.round((a.x + (b.x - a.x) * s / n - origin.x) / step), j = Math.round((a.z + (b.z - a.z) * s / n - origin.z) / step);
      if (i < 0 || j < 0 || i >= size || j >= size || grid.walk[j * size + i] !== '.' || (s > 0 && wet(j * size + i))) return false;
    }
    return true;
  };

  // The centre to go around: the boss; otherwise the enemies close by, nearer ones counting more.
  let C = target.position;
  if (!boss) {
    const near = entities.filter(e => dist(me, e.position) < 10);
    if (near.length > 1) {
      let wx = 0, wz = 0, w = 0;
      for (const e of near) { const k = 1 / Math.max(1, dist(me, e.position)); wx += e.position.x * k; wz += e.position.z * k; w += k; }
      C = { x: wx / w, z: wz / w };
    }
  }
  const rl = Math.max(0.1, Math.hypot(me.x - C.x, me.z - C.z));
  const rh = { x: (me.x - C.x) / rl, z: (me.z - C.z) / rl };
  const tangent = s => ({ x: -rh.z * s, z: rh.x * s });
  const desired = opts.desired || range * (boss ? 0.85 : 0.75);

  // Right next to one of them.
  const melee = p => {
    let d = 0;
    for (const e of entities) {
      const r = isBossE(e) ? (opts.bossMelee || 5.5) : 3;
      const ed = dist(p, e.position);
      if (ed < r) d += (r - ed) * (r - ed) * (isBossE(e) ? 1.5 : 2);
    }
    // Iteration 21: 3.5 (was 1.5) - run-031 stood 6.6-8 m from the shielded Infernus for 6 s (its Atk reaches 6.8-7.5 m).
    if (opts.keep) { const kd = dist(p, opts.keep.pos); if (kd < opts.keep.r) d += (opts.keep.r - kd) * (opts.keep.r - kd) * KEEP_W; }
    return d;
  };
  // On the line a projectile is flying along, before it gets there.
  const inLine = p => {
    let d = 0;
    for (const s of shots) {
      if (s.homing || s.eta > 1.6) continue;
      const ax = p.x - s.position.x, az = p.z - s.position.z;
      const along = ax * s.heading.x + az * s.heading.z;
      if (along < 0 || along > s.remaining + 1) continue;
      const side = Math.abs(ax * s.heading.z - az * s.heading.x);
      if (side < s.radius + 0.9) d += 5 * (1.6 - Math.min(1.6, along / Math.max(1, s.speed)));
    }
    return d;
  };
  // Walking there goes through one of them.
  const through = p => {
    let d = 0;
    for (const e of entities) if (segDist(e.position, me, p) < (isBossE(e) ? 3 : 1.6)) d += isBossE(e) ? 4 : 3;
    return d;
  };

  // The shot that will hit where the hero stands: step off its line, to whichever side is nearer.
  // Iteration 51: a burning shot (burnShot: Hellfire Spider's LavaAtk) from farther off and wider, and weighed more (BURN_DODGE).
  let dodge = null;
  for (const s of shots) {
    const burn = burnShot(s);
    if (s.homing || s.eta > (burn ? BURN_DODGE.eta : 1.0) || s.miss > s.radius + (burn ? BURN_DODGE.miss : 0.8)) continue;
    const ax = me.x - s.position.x, az = me.z - s.position.z;
    const cross = ax * s.heading.z - az * s.heading.x;
    const sgn = Math.abs(cross) > 0.1 ? Math.sign(cross) : orbitSide;
    dodge = { x: s.heading.z * sgn, z: -s.heading.x * sgn, w: burn ? BURN_DODGE.w : 2.5 };
    break;
  }

  // Iteration 35: a sweep the hero stands in (Azurak's roll, a beam, a charge: a box much longer along its facing than wide,
  // landing within SWEEP_T s) is left sideways - straight across it is the shortest way out; along it (toward the ball, or
  // running with it) keeps the hero in it longer (tests/iter35: a diagonal step toward Azurak took 0.9 s to get out).
  const sweeps = areas.filter(a => a.shape === 'box' && a.facing && a.corners && a.left >= 0 && a.left < SWEEP_T && sweepLong(a) && areaDepth(me, a, 0.4) > 0).map(a => a.facing);
  const hk = grid.hero ? grid.hero.j * size + grid.hero.i : -1;
  const cells = [];
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8 || (k !== hk && wet(k))) continue;
    const p = cellPos(k);
    const md = dist(me, p);
    let area = 0;
    for (const a of areas) { const dp = areaDepth(p, a); if (dp > 0) area += 10 + 6 * a.fill + 3 * dp; }
    // Iteration 22: inside an Infernus eruption whose throw ends on the lava (knockLava; run-032/033 died so).
    if (area > 0 && hazard && knockLava(grid, p, areas)) area += KNOCK_W;
    // Iteration 46: the risk from Doom's fireballs' paths there (opts.doom: doomRisk) - walking and the dash's landing keep off them.
    const c = { k, p, md, clear: clear[k], reach: reach[k], melee: melee(p), line: inLine(p) + area + (opts.doom ? opts.doom(p) : 0), area, cross: crossRed(me, p, areas) };
    c.fit = opts.approach ? -2 * dist(p, opts.approach) : -Math.abs(dist(p, C) - desired);
    c.room = 0.9 * Math.min(clear[k], 5) - (clear[k] <= 1 ? 5 : clear[k] === 2 ? 1.5 : 0) - (hazard && clear[k] <= 2 ? 2 : 0);
    const v = md > 0.1 ? { x: (p.x - me.x) / md, z: (p.z - me.z) / md } : { x: 0, z: 0 };
    c.v = v;
    c.dodge = dodge ? dodge.w * (v.x * dodge.x + v.z * dodge.z) : 0;
    c.sweep = 0;
    for (const fw of sweeps) c.sweep += SWEEP_W * Math.abs(v.x * fw.x + v.z * fw.z);
    cells.push(c);
  }
  const here = cells.find(c => c.k === hk);
  const sideways = (c, s) => { const t = tangent(s); return c.v.x * t.x + c.v.z * t.z; };
  const mo = opts.momentum && (opts.momentum.x || opts.momentum.z) ? opts.momentum : null;
  // Iteration 44: near a wall (the hero's cell's clear <= 3) the circling leans to the open side (fight()'s pinRead: the middle of
  // the longest run of ways a step can take) - OPEN_W at clear 2, half of it at 3 - so the orbit does not drift along the wall into
  // a corner (run-051's Azurak room: room 1-3 for seconds with them round the hero). Not while walking to a place (approach).
  const hereClear = here ? here.clear : null;
  const od = opts.openDir && !opts.approach && hereClear != null && hereClear <= 3 ? opts.openDir : null;
  const openPull = od ? OPEN_W * (4 - hereClear) / 2 : 0;
  const walkScore = (c, s) => (opts.approach ? c.fit : 2.2 * sideways(c, s) + 0.5 * c.fit) + c.room - c.melee - c.line - c.cross - through(c.p) + c.dodge - c.sweep - 0.1 * c.reach +
    (mo ? 3 * (c.v.x * mo.x + c.v.z * mo.z) : 0) + (od ? openPull * (c.v.x * od.x + c.v.z * od.z) : 0);
  const inArea = here ? here.area > 0 : false;
  const bestFor = s => {
    let best = null;
    for (const c of cells) {
      if (c.md < (inArea ? 0.8 : 2) || c.md > 5.5) continue;
      const score = walkScore(c, s);
      if (!best || score > best.score) best = { ...c, score };
    }
    return best;
  };
  // Iteration 33: a side given (Infernus's breath - away from its facing) is kept: no flip.
  if (opts.side) orbitSide = opts.side;
  let best = bestFor(orbitSide);
  const other = bestFor(-orbitSide);
  // The way round is blocked (a wall, a chasm, more of them): go round the other way.
  if (!opts.side && other && (!best || other.score > best.score + 2) && Date.now() - orbitFlippedAt > 1200) {
    orbitSide = -orbitSide; orbitFlippedAt = Date.now(); best = other;
  }

  // The first steps of the path to it: walk back down the reach gradient from the best cell.
  let way = best;
  // With hazard ground about, the farthest cell of that path whose straight line from the hero stays on
  // standable cells (string-pulled, as walkSafe's legs) - not a straight line over a tongue of lava.
  if (best && hazard && !lineOk(me, best.p)) {
    const chain = [best.k];
    for (let k = best.k; reach[k] > 1;) {
      const i = k % size, j = Math.floor(k / size);
      let next = -1;
      for (let dj = -1; dj <= 1 && next < 0; dj++) for (let di = -1; di <= 1; di++) {
        const ni = i + di, nj = j + dj;
        if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
        const n = nj * size + ni;
        if (reach[n] === reach[k] - 1) { next = n; break; }
      }
      if (next < 0) break;
      chain.push(next); k = next;
    }
    const k = chain.find(q => lineOk(me, cellPos(q))) ?? chain[chain.length - 1];
    way = { k, p: cellPos(k), pulled: true };
  } else if (best && best.reach > 3 && !hazard) {
    // Iteration 43: of the cells one step nearer, the one with the least red (a pool counts), then the nearest the straight
    // line to the best cell - not the first in scan order, which on open ground is always a diagonal: run-047's Dark Moon
    // (she stood 7.6 m from the Ink ground's centre) and run-051's White Night: the best cell outside the ground, 4-5 m off,
    // and the first steps to it 0.5-1.5 m inside it - walked each look (10:27:38-43, 142 + 106; 11:36:47-48, 76).
    // tests/iter43.test.mjs.
    const redAt = new Map(cells.map(c => [c.k, c.area]));
    let k = best.k;
    while (reach[k] > 3) {
      const i = k % size, j = Math.floor(k / size);
      let next = -1, nBest = null;
      for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) {
        const ni = i + di, nj = j + dj;
        if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
        const n = nj * size + ni;
        if (reach[n] !== reach[k] - 1) continue;
        const s = 100 * (redAt.get(n) || 0) + segDist(cellPos(n), me, best.p);
        if (nBest === null || s < nBest) { nBest = s; next = n; }
      }
      if (next < 0) break;
      k = next;
    }
    way = { k, p: cellPos(k) };
  }

  // A dash lands 3.5-5.5 m away, past them if need be, where there is room and nothing next to it.
  const dashScore = c => 1.2 * Math.min(c.clear, 5) - (c.clear <= 1 ? 6 : 0) - c.melee - c.line + 1.2 * sideways(c, orbitSide) + 0.3 * c.fit + c.dodge - c.sweep;
  // With hazard ground about, not onto a cell next to it (the dash itself crosses lava unhurt: a
  // displacement, LavaLand_Lava skips isDisplacing).
  const dash = cells.filter(c => c.md > 3.5 && c.md < 5.5 && !(hazard && c.clear < 2)).sort((x, y) => dashScore(y) - dashScore(x))[0];
  // A step (not a dash) that gets out of every red shape, when standing in one.
  const walkOut = !inArea || cells.some(c => c.area === 0 && c.md >= 0.8 && c.md <= 5.5);
  return { best, way, here, dash, dodge, centre: C, inArea, walkOut };
}

// Whether to dash now, and why. The reasons come in two kinds:
//   hard  red about to land on the hero (areaUrgent), red a step cannot leave (trapped), a shot
//         about to hit - what the dash is for;
//   soft  crowding: one of them on top of the hero, cornered, the boss close or charging, low
//         health up close - walking would mostly do.
// The dash (Lacerta's Nimble Dodge) has 2 charges that come back one at a time, ~3.3 s each
// (run-003's boss trace). Every boss fight in run-001..004 opened with both charges spent on soft
// reasons within 0.4 s ("boss charging" + "boss close", no red anywhere): at Skoll (run-003) that
// left the dash on cooldown for the AuraBlade box that took 239, and both charges went on the
// first sword wave of Death From Above, so the next two waves (4 hits, 536 -> 0) found no dash.
// So in a boss fight a soft reason spends a charge only while another stays in reserve.
// A boss fight is a zone boss (monsterType Boss), not a miniboss: run-005 and run-006 held the dash
// ("boss close") in combat rooms against a miniboss Leaf Hound / Scorching Wolf, at 54/628 hp once.
// Red read from an untimed blob only (no strike under the hero, `blobOnly`) is a guess, not a blow
// about to land: a blob has no fuse, so it is read as landing in 1 s. At Infernus (run-006) the
// boss's blows are all strikes (Meteor, Stomp, its eruptions, Swipe, Jump_Land); two of the five
// dashes went on blobs that hit nothing - a 10.6 m one on the boss and a 20.6 m one while it stood
// invulnerable, the hero already walking out of it - and the Stomp's eruptions 3.5 s later found
// one charge, whose dash landed in another eruption with no second one to leave it: 119 (192 -> 73),
// then the burn. So in a zone-boss fight a blob-only hard reason keeps the last charge too.
//   s: { areaUrgent, trapped, shotNow, onTop, cornered, bossNear, bossRushing, lowClose,
//        bossFight (a zone boss is here), charges (undefined: not known - no reserve),
//        blobOnly (the red behind areaUrgent / trapped is untimed blobs only) }
// A blind boss (iteration 12; `blind`): one whose big blows come with no red the hero stood in first -
// Dark Moon (run-016: Blade 241, ShortDash 361, Blade 241 in 4 s, 1020 -> 141, with "dash held: on top
// of us / boss close - keeping the last charge for the red" twice and a charge ready: the red never
// came). At such a boss the reserve is not kept: its closing in is the only warning there is.
// Returns { kind: 'hard'|'soft', why } to dash, { kind: 'held', why } for a soft reason kept
// back, or null.
// keepOne (iteration 15, LavaLand): a soft reason keeps the last charge in every fight there, not only at a
// zone boss - run-021's first LavaLand room spent both charges on "cornered" dashes (the lava cramps every
// room) and had none for Magmadon's Charge 0.3 s later. Hard reasons (the red, a shot) are not affected.
// Iteration 30 (the user watched it: dashes spent on crowding, then none for the red): the reserve in EVERY
// fight, for every soft reason, "low health" too - a soft reason dashes only with both charges ready, so the
// red always finds one (keepOne is now the rule everywhere; the flag is still accepted). Runs 030-040: 255 soft
// dashes, 77 of them the second within 3.3 s of another (both charges gone on crowding), 102 left no charge;
// run-034 died so - in Sky's Combat_1_3 at 262/764 "low health" dashed twice in 0.4 s (nearest 3.6 and 3.2 m),
// a Star Seed blob found 0 charges, the one charge back went on "cornered" (nearest 3.9 m), and Big Baam's beam
// box 0.9 s later found none: 171, dead 5 s after. At blind bosses the last charge still goes (as before).
// And a soft dash only where it gets the hero clear (`landWorse`, from fight(): the dash cell in red, or no
// farther from the nearest of them than the hero is now plus SOFT_GAIN) - else it walks: 6 of 16 traced
// low-health/cornered dashes in runs 031-034 ended nearer an enemy than they began. Hard reasons dash as before.
// Returns { kind: 'held', why, walk } for a soft reason whose dash would land no clearer.
function dashChoice(s) {
  const spare = s.charges != null && s.charges < 2 && !s.blind;   // a soft dash now would spend the last charge
  const reserve = s.bossFight && spare;
  const red = s.areaUrgent ? 'area' : s.trapped ? 'trapped' : null;
  if (red && !(s.blobOnly && reserve)) return { kind: 'hard', why: red };
  if (s.shotNow) return { kind: 'hard', why: 'shot incoming' };
  if (red) return { kind: 'held', why: red + ' (a blob, no timer)' };
  const soft = s.onTop ? 'on top of us' : s.cornered ? 'cornered' : s.bossNear ? 'boss close' :
    s.bossRushing ? 'boss charging' : s.lowClose ? 'low health' : null;
  if (!soft) return null;
  if (spare) return { kind: 'held', why: soft };
  if (s.landWorse) return { kind: 'held', why: soft, walk: s.landWorse };
  return { kind: 'soft', why: soft };
}
// Iteration 30: a soft dash must end at least this much farther from the nearest enemy than the hero is now.
const SOFT_GAIN = 1.5;
// The soft triggers' reach (iteration 30; were 4.5 m for "cornered" and 5 m for "low health"): within melee reach.
const SOFT_NEAR = 3;
// Whether a soft dash to `cell` gets the hero clear (dashChoice's landWorse): null when it does, else why not.
// cell: plan()'s dash cell ({ p, area }); enemies: [{ position }]; me: the hero.
function softLanding(cell, me, enemies) {
  if (!cell || !cell.p) return 'no cell';
  if (cell.area > 0) return 'it lands in red';
  if (!enemies.length) return null;
  let now = Infinity, then = Infinity;
  for (const e of enemies) { now = Math.min(now, dist(me, e.position)); then = Math.min(then, dist(cell.p, e.position)); }
  return then < now + SOFT_GAIN ? `it lands ${then.toFixed(1)}m from one of them, ${now.toFixed(1)}m now` : null;
}
// "On top of us" only while the nearest is not already falling behind (iteration 30): the same enemy as at the last look
// and >= 0.3 m farther now means the walk is shaking it off. prev: { id, d } from the last look, or null.
const closing = (prev, id, d) => !(prev && prev.id === id && d > prev.d + 0.3);

// ----- iteration 44: pinned against a wall with them round the hero, and the break-out (pure) -----
// The user watched Azurak's room (run-051 11:40:55-11:41:07): the hero at an island's edge ("room 1-3" in the trace), 7-11
// Phase Bugs and Dread Bugs round it, "dash held: on top of us - keeping the last charge for the red" every look for seconds,
// "not moving - circling the other way" flipping the orbit, and 198 + 395 + 198 from the Phase Bugs' dashes and blows in 3 s.
// The circle planner (plan()'s walkScore) keeps to the orbit; with a wall behind and them in front no orbit cell is good, the
// soft dash waits for a second charge (iteration 30) that the red keeps taking, and a flip only walks the hero along the wall.
// Runs 040-051 (bot.log: "dash held: on top of us / cornered" and "not moving" within 5 s of each other, >= 2 a cluster): 36
// such clusters, ~200 s, 2524 hp taken in them - 791 of it in run-051's Azurak room, 333 in run-047's.
// So: pinned is read from the grid (pinRead: the room round the hero, the ways a step can go that meet neither a wall nor one
// of them; pinStep: cramped for PIN_T s, or "not moving" twice in PIN_STALL_T s with one of them near), and then the hero
// breaks out to the most open ground within ~15 m of walking that a way reaches passing the fewest of them (openArea: a
// Dijkstra over the grid that pays for passing next to one): through the gap when the way finds one, else the weak add in the
// way when a ready skill kills it, else a dash along the way (one charge will do when pinned - being cornered IS the danger -
// unless timed red is about to land near), else the walk along the least crowded way (breakPlan).
const PIN_ROOM = 2, PIN_NEAR = 4, PIN_T = 1.5, PIN_STALL_N = 2, PIN_STALL_T = 4;
const PIN_DIRS = 16, PIN_RAY = 3, PIN_FOE_W = 1.1;
const STATIC_FOE = /AzurakRollPillar|AzurakMonsterSpawner|InfernusPillar/;   // stand still, never swing (navmesh obstacles)
const OPEN_R = 3, OPEN_PATH = 15, OPEN_FOE = 4, GAP_R = 1.2, BREAK_MAX = 4000, BREAK_COOL = 1500, OPEN_W = 1.5;
// The grid's cell under a point (-1 off the grid).
function pinCell(g, p) {
  const i = Math.round((p.x - g.origin.x) / g.step), j = Math.round((p.z - g.origin.z) / g.step);
  return i < 0 || j < 0 || i >= g.size || j >= g.size ? -1 : j * g.size + i;
}
// The enemies that close in and swing: not the ones that stand (pillars, spawners), not the dead.
const pinFoes = enemies => (enemies || []).filter(e => e && e.position && e.alive !== false && !STATIC_FOE.test(e.type || ''));
// The room round the hero on /nav/grid and the ways a step can go.
//   g: /nav/grid (reach < 0: not walkable or not reachable - a wall), me: the hero, enemies: /entities enemies,
//   o.bossReach: how far a boss's swing counts (5.5; Primus 6.5).
// Returns { room (the hero's cell's clear, m), foes (within PIN_NEAR, a boss within its reach + 1), weight (a boss counts 2),
//   free (PIN_DIRS booleans: a step that way meets no wall within PIN_RAY m and passes no foe within PIN_FOE_W m), run (the
//   longest run of free ways), openDir (the middle of that run, a unit vector; null when every way or none is free),
//   cramped (enough of them and too few ways out: room <= PIN_ROOM with the run <= 6 of 16, or anywhere with the run <= 3) }.
function pinRead(g, me, enemies, o = {}) {
  const out = { room: null, foes: [], weight: 0, free: [], run: PIN_DIRS, openDir: null, cramped: false };
  if (!g || !g.reach || !g.clear) return out;
  const hk = g.hero ? g.hero.j * g.size + g.hero.i : pinCell(g, me);
  out.room = hk >= 0 ? g.clear[hk] : null;
  const reachB = o.bossReach || 5.5;
  out.foes = pinFoes(enemies).filter(e => dist(me, e.position) < (isBossE(e) ? reachB + 1 : PIN_NEAR));
  out.weight = out.foes.reduce((s, e) => s + (isBossE(e) ? 2 : 1), 0);
  // A boss within its reach + 4 m: "not moving" twice by it is pinned too (run-047 10:31:33-35: "not moving" x2 at 7.7 m from Azurak,
  // then his Atk 222 + 111).
  out.bossNear = pinFoes(enemies).some(e => isBossE(e) && dist(me, e.position) < reachB + 4);
  const stat = (enemies || []).filter(e => e && e.position && e.alive !== false && STATIC_FOE.test(e.type || '') && dist(me, e.position) < PIN_RAY + 1);
  const depth = [];   // each way: the room where its ray ends (clear at PIN_RAY m; 0 when blocked)
  for (let n = 0; n < PIN_DIRS; n++) {
    const a = 2 * Math.PI * n / PIN_DIRS, u = { x: Math.cos(a), z: Math.sin(a) };
    let free = true;
    for (let r = 1; r <= PIN_RAY && free; r += 1) {
      const k = pinCell(g, { x: me.x + u.x * r, z: me.z + u.z * r });
      if (k >= 0 && g.reach[k] < 0) free = false;
      if (r === PIN_RAY) { const c = k >= 0 ? g.clear[k] : 6; depth.push(Math.min(6, c)); }
    }
    if (depth.length <= n) depth.push(0);
    for (const e of out.foes.concat(stat)) {
      if (!free) break;
      const ax = e.position.x - me.x, az = e.position.z - me.z, along = ax * u.x + az * u.z;
      if (along <= 0 || along > PIN_RAY + 0.5) continue;
      if (Math.abs(ax * u.z - az * u.x) < (isBossE(e) ? 2 : PIN_FOE_W)) free = false;
    }
    out.free.push(free);
  }
  // The longest run of free ways (run), and the open side: the run scoring best on the most room at a ray's end (clear) + half its length - a slot
  // along the wall (clear 1-2 at its end) loses to a gap out into the open.
  let best = 0, oRun = 0, oEnd = -1, oRoom = -1;
  for (let s = 0, cur = 0, room = 0; s < 2 * PIN_DIRS; s++) {
    if (out.free[s % PIN_DIRS]) { cur++; room = Math.max(room, depth[s % PIN_DIRS]); } else { cur = 0; room = 0; }
    const c = Math.min(cur, PIN_DIRS);
    if (c > best) best = c;
    if (c > 0 && c < PIN_DIRS && room + 0.5 * c > oRoom) { oRun = c; oEnd = s; oRoom = room + 0.5 * c; }
  }
  out.run = best;
  if (best > 0 && best < PIN_DIRS) {
    const mid = 2 * Math.PI * (oEnd - (oRun - 1) / 2) / PIN_DIRS;
    out.openDir = { x: Math.round(Math.cos(mid) * 1000) / 1000, z: Math.round(Math.sin(mid) * 1000) / 1000 };
  }
  out.cramped = out.weight >= 2 && ((out.room != null && out.room <= PIN_ROOM && best <= 6) || best <= 3);
  return out;
}
// Pinned yet? st: { since, stalls: [ms of "not moving"] } kept by fight(); read: pinRead's; now: ms.
// Returns 'room' (cramped for PIN_T s), 'stall' ("not moving" PIN_STALL_N times in PIN_STALL_T s, one of them or a boss near) or null.
function pinStep(st, read, now) {
  if (read.cramped) { if (!st.since) st.since = now; } else st.since = 0;
  st.stalls = (st.stalls || []).filter(t => now - t <= PIN_STALL_T * 1000);
  if (st.since && now - st.since >= PIN_T * 1000) return 'room';
  if (st.stalls.length >= PIN_STALL_N && (read.foes.length >= 1 || read.bossNear)) return 'stall';
  return null;
}
// The most open ground within OPEN_PATH m of walking, and the way there that passes the fewest of them.
//   g: a /nav/grid (radius ~14), me, enemies, areas: the red (plan()'s), o: { bossReach, weak (e => the hero kills it on the
//   way: passing it costs a third), keep: { pos, r } (a spot kept off:
//   a shielded boss) }.
// A Dijkstra from the hero's cell over walkable neighbours: a metre costs 1, a metre next to one of them (within GAP_R + 0.6)
// up to 6 more, a metre in red 4 more. Each cell reached within OPEN_PATH m and 3 m or more off is weighed: its open ground (the
// cells within OPEN_R m with clear >= 2, out of the red and no enemy within 2 m, in m2), its room, the enemies within OPEN_FOE m
// of it (a boss within its reach + 1 counts 3x), the extra its way paid. Returns the best { p, k, area, room, len, extra, path
// (points from the hero), crossed (the enemies within GAP_R m of the way, 1 m ahead of the hero or more), gap (the way's
// nearest pass to one, m), dir, way (the first point 4 m off along it), dashTo (the farthest point 3.5-5.5 m straight along
// the way, out of the red, clear >= 2 and not next to one of them) } or null.
function openArea(g, me, enemies, areas = [], o = {}) {
  if (!g || !g.reach || !g.clear) return null;
  const { origin, step, size, reach, clear } = g;
  const N = size * size, pos = k => ({ x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step });
  const hk = g.hero ? g.hero.j * size + g.hero.i : pinCell(g, me);
  if (hk < 0 || reach[hk] < 0) return null;
  const reachB = o.bossReach || 5.5;
  const foes = pinFoes(enemies);
  // What stands (Azurak's roll pillars, spawners): a body the way goes round, whether or not the navmesh carved it.
  const stat = (enemies || []).filter(e => e && e.position && e.alive !== false && STATIC_FOE.test(e.type || ''));
  const red = (areas || []).filter(a => a && !a.keepOut);
  const foeD = new Float64Array(N).fill(99), inRed = new Uint8Array(N), free = new Uint8Array(N), body = new Uint8Array(N), foeC = new Float64Array(N);
  for (let k = 0; k < N; k++) {
    if (reach[k] < 0) continue;
    const p = pos(k);
    if (k !== hk && stat.some(e => dist(p, e.position) < 1.2)) { body[k] = 1; continue; }
    for (const e of foes) {
      const dd = dist(p, e.position) - (isBossE(e) ? Math.max(0, reachB - 2) : 0);
      if (dd < foeD[k]) foeD[k] = dd;
      // Passing next to one: a boss twice, a weak add (o.weak: one the hero kills on the way) a third.
      if (dd < GAP_R + 0.6) foeC[k] += (isBossE(e) ? 2 : o.weak && o.weak(e) ? 0.35 : 1) * (GAP_R + 0.6 - dd) / (GAP_R + 0.6);
    }
    for (const a of red) if (areaDepth(p, a, 0.3) > 0) { inRed[k] = 1; break; }
    if (o.keep && dist(p, o.keep.pos) < o.keep.r) inRed[k] = 1;
    free[k] = clear[k] >= 2 && !inRed[k] && foeD[k] >= 2 ? 1 : 0;
  }
  // Dijkstra (N is ~900: a plain scan for the next node is quick enough).
  const cost = new Float64Array(N).fill(Infinity), len = new Float64Array(N).fill(Infinity), prev = new Int32Array(N).fill(-1), done = new Uint8Array(N);
  cost[hk] = 0; len[hk] = 0;
  const maxLen = OPEN_PATH / step;
  for (;;) {
    let k = -1, c = Infinity;
    for (let q = 0; q < N; q++) if (!done[q] && cost[q] < c) { c = cost[q]; k = q; }
    if (k < 0) break;
    done[k] = 1;
    if (len[k] >= maxLen) continue;
    const i = k % size, j = Math.floor(k / size);
    for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) {
      if (!di && !dj) continue;
      const ni = i + di, nj = j + dj;
      if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
      const n = nj * size + ni;
      if (done[n] || reach[n] < 0 || body[n]) continue;
      if (di && dj && (reach[j * size + ni] < 0 || reach[nj * size + i] < 0)) continue;   // no corner cutting
      const m = di && dj ? Math.SQRT2 : 1;
      const w = m * (1 + 6 * foeC[n] + 4 * inRed[n] + (clear[n] <= 1 ? 1.5 : 0));   // hugging a wall costs too
      if (cost[k] + w < cost[n]) { cost[n] = cost[k] + w; len[n] = len[k] + m; prev[n] = k; }
    }
  }
  const R = Math.round(OPEN_R / step);
  let best = null;
  for (let k = 0; k < N; k++) {
    if (!done[k] || !isFinite(cost[k]) || len[k] > maxLen || inRed[k] || body[k]) continue;
    const p = pos(k), md = dist(me, p);
    if (md < 3) continue;
    const i = k % size, j = Math.floor(k / size);
    let cnt = 0;
    for (let dj = -R; dj <= R; dj++) for (let di = -R; di <= R; di++) {
      if (di * di + dj * dj > R * R) continue;
      const ni = i + di, nj = j + dj;
      if (ni < 0 || nj < 0 || ni >= size || nj >= size) continue;
      if (free[nj * size + ni]) cnt++;
    }
    const area = cnt * step * step;
    let crowd = 0;
    for (const e of foes) {
      const dd = dist(p, e.position);
      if (isBossE(e)) { if (dd < reachB + 1) crowd += 3 * (reachB + 1 - dd); }
      else if (dd < OPEN_FOE) crowd += OPEN_FOE - dd;
    }
    const extra = (cost[k] - len[k]) * step;
    const score = 0.6 * Math.min(area, 28) + 0.8 * Math.min(clear[k], 6) - 2.5 * crowd - 1.2 * extra - 0.15 * len[k] * step;
    if (!best || score > best.score) best = { k, p, area, room: clear[k], len: Math.round(len[k] * step * 10) / 10, extra: Math.round(extra * 10) / 10, score, crowd };
  }
  if (!best) return null;
  const chain = [];
  for (let k = best.k; k >= 0; k = prev[k]) chain.push(k);
  chain.reverse();
  best.path = chain.map(pos);
  best.path[0] = { x: me.x, z: me.z };
  const dl = dist(me, best.p) || 1;
  best.dir = { x: (best.p.x - me.x) / dl, z: (best.p.z - me.z) / dl };
  // Who the way passes: 1 m along it or more, within GAP_R of it.
  best.crossed = []; best.gap = 99;
  let walked = 0;
  for (let s = 1; s < best.path.length; s++) {
    walked += dist(best.path[s - 1], best.path[s]);
    if (walked < 1) continue;
    for (const e of foes) {
      const dd = dist(best.path[s], e.position) - (isBossE(e) ? Math.max(0, reachB - 2) : 0);
      if (dd < best.gap) best.gap = dd;
      if (dd < GAP_R && !best.crossed.includes(e)) best.crossed.push(e);
    }
  }
  best.gap = Math.round(best.gap * 10) / 10;
  best.way = best.path.find(q => dist(me, q) >= 4) || best.p;
  const nearNow = foes.reduce((m, e) => Math.min(m, dist(me, e.position)), 99);
  best.dashTo = null;
  for (const q of best.path) {
    const qd = dist(me, q), k = pinCell(g, q);
    if (qd < 3.5 || qd > 5.5 || k < 0 || inRed[k] || clear[k] < 2) continue;
    const nearThen = foes.reduce((m, e) => Math.min(m, dist(q, e.position)), 99);
    if (nearThen < 2 && nearThen < nearNow + 1) continue;
    best.dashTo = q;
  }
  return best;
}
// How to break out along openArea's way. ctx: { dashReady, charges (undefined: not known), redSoon (timed red about to land
// near the hero), killNow (e => a ready skill kills it now), killSoon (e => two basic attacks do) }.
// Returns { how: 'gap' | 'kill' | 'dash' | 'push', to, target?, why } or null:
//   gap  - the way passes none of them: walk it;
//   kill - the one weak add in the way, that a ready skill kills now (or, with no dash to spend, the attacks soon);
//   dash - along the way (both charges, or one when no timed red is about to land near: pinned IS the danger);
//   push - none of those: walk the least crowded way (the dash when a charge comes back).
function breakPlan(oa, ctx = {}) {
  if (!oa) return null;
  if (!oa.crossed.length) return { how: 'gap', to: oa.way, why: `through the gap (${oa.gap}m from the nearest on the way)` };
  const lone = oa.crossed.length === 1 ? oa.crossed[0] : null;
  const who = e => `${e.name || e.type} (${Math.round(effHp(e))}hp)`;
  if (lone && !isBossE(lone) && ctx.killNow && ctx.killNow(lone)) return { how: 'kill', to: oa.way, target: lone, why: `killing ${who(lone)} in the way first` };
  const oneOk = ctx.charges == null || ctx.charges >= 2 || (ctx.charges >= 1 && !ctx.redSoon);
  if (ctx.dashReady && oa.dashTo && oneOk) return { how: 'dash', to: oa.dashTo, why: `a dash through them (${oa.crossed.length} in the way${ctx.charges === 1 ? ', the last charge' : ''})` };
  if (lone && !isBossE(lone) && ctx.killSoon && ctx.killSoon(lone)) return { how: 'kill', to: oa.way, target: lone, why: `killing ${who(lone)} in the way first` };
  return { how: 'push', to: oa.way, why: `the least crowded way (${oa.crossed.length} in it${ctx.dashReady && ctx.redSoon && oa.dashTo ? '; the last charge kept: red about to land' : ctx.dashReady && !oa.dashTo ? '; no clear landing for a dash' : ''})` };
}
// The walk's point on the way now: the way's first point 3 m or more ahead of the one the hero is nearest.
function breakWay(path, me) {
  if (!path || !path.length) return null;
  let i = 0;
  for (let s = 1; s < path.length; s++) if (dist(me, path[s]) < dist(me, path[i])) i = s;
  for (let s = i; s < path.length; s++) if (dist(me, path[s]) >= 3) return path[s];
  return path[path.length - 1];
}
// Whether the break-out is over: at the open ground, out in the open (room >= 3, at most one of them within PIN_NEAR, none
// within 2 m), or BREAK_MAX spent. Returns why, or null.
function breakOver(on, read, me, now) {
  if (on.oa && dist(me, on.oa.p) < 1.5) return 'there';
  const near2 = read.foes.some(e => dist(me, e.position) < 2);
  if (read.room != null && read.room >= 3 && read.foes.length <= 1 && !near2) return 'in the open';
  if (now - on.since > BREAK_MAX) return 'time';
  return null;
}
// ----- end of iteration 44's pure part

// ----- iteration 33: Infernus's breath (pure) -----
// Infernus's BreathFire (run-042's death: 6 x 85 + the burn, 520 -> 0 in 0.9 s, Infernus at 1422/12841). Decompiled
// (history/it13/Ai_Mon_LavaLand_BossInfernus_BreathFire.cs, history/it33/..._Projectile.cs): a 3 s channel (spawns 8 x
// spawnInterval 0.375 s; Unstoppable, every action of Infernus blocked; then a 2 s daze) in which Infernus stands and
// turns toward the hero - Quaternion.RotateTowards at lerp(20, 120, i / 8) deg/s for the i-th jet, the hero's own
// position, no lead - and every 0.375 s sends a jet straight along its facing: a StandardProjectile (NOT homing - /threats
// says homing false; the trace's "homing" is an empty list), its collision radius growing along its flight
// (collisionRadiusRange), piercing, hitting an entity at most every minHitInterval. So it is a flame-thrower that
// sweeps after the hero: 20 deg/s at first, 107.5 deg/s for the last jet, 191 deg in all. What beats it is angular
// speed: a hero walking ~5 m/s round Infernus at r m turns 286 / r deg/s - 64 deg/s at 4.5 m, 41 at 7, 27 at 10.5.
// run-030 and run-034 circled at 6-7 m through 1.2-1.4 s of it with no hit (Infernus died in it); run-042 was at 4.9 m
// when it began, dashed twice on "shot incoming" (each dash mostly outward: 4.9 -> 8.0 -> 10.9 m) and at 10.5 m, both
// charges gone, walked 27 deg/s while the late jets turned 82-107: caught. Dashing away is the wrong move - it slows
// the hero's turn about Infernus and the jets widen with distance. So while it breathes: circle Infernus close
// (BREATH_R, its swing blocked by the channel), always away from its facing (the newest jet's heading), no
// "shot incoming" / "boss close" dashes for the jets, and a dash only when a jet will hit where the hero is going - then
// sideways round Infernus (breathDashCell: >= BREATH_GAIN deg further from its facing, not farther out).
const BREATH = /BossInfernus_BreathFire_Projectile/;
const BREATH_T = 3.2, BREATH_R = 4.5, BREATH_MELEE = 3.5, BREATH_GAIN = 30, BREATH_ETA = 0.45, BREATH_BODY = 0.4;
const angDeg = v => Math.atan2(v.z, v.x) * 180 / Math.PI;
const angDiff = (a, b) => ((a - b) % 360 + 540) % 360 - 180;   // a - b in [-180, 180)
// Infernus's jet heading's way round: the hero's side of its facing (+1: counter-clockwise, as orbitSide).
// st: { t0, last, facing, side, dashAt } kept by fight(); shots: /threats projectiles; boss: Infernus; vel: the hero's
// velocity (m/s, {x, z}) or null. null when it is not breathing, else { el (s since the first jet), facing, gap (deg,
// the hero's bearing from Infernus minus its facing), side, d, hit (the jet that meets the hero where it is going,
// within BREATH_ETA s), jets, reach (m, the farthest end of a jet's flight) }.
function breathRead(st, shots, boss, me, now, side0 = 1, vel = null) {
  const jets = (shots || []).filter(s => BREATH.test(s.type || '') && s.position && s.heading);
  if (jets.length) {
    if (!st.t0 || now - st.last > 1500) { st.t0 = now; st.side = null; st.facing = null; st.said = false; st.noCellSaid = false; }
    st.last = now;
  }
  if (!boss || !(jets.length || (st.t0 > 0 && now - st.t0 < BREATH_T * 1000))) return null;
  if (jets.length) {
    const newest = jets.reduce((b, s) => !b || dist(s.position, boss.position) < dist(b.position, boss.position) ? s : b, null);
    st.facing = { x: newest.heading.x, z: newest.heading.z };
  }
  const r = { x: me.x - boss.position.x, z: me.z - boss.position.z };
  const d = Math.hypot(r.x, r.z);
  const gap = st.facing ? angDiff(angDeg(r), angDeg(st.facing)) : 0;
  // Away from its facing; within 10 deg of it the side already taken (or the way the hero is circling).
  if (Math.abs(gap) >= 10 || !st.side) st.side = Math.abs(gap) >= 10 ? Math.sign(gap) : (side0 || 1);
  let hit = null, reach = 0;
  for (const s of jets) {
    reach = Math.max(reach, dist(boss.position, { x: s.position.x + s.heading.x * (s.remaining || 0), z: s.position.z + s.heading.z * (s.remaining || 0) }));
    // Where the hero will be when the jet gets there (the mod's `miss` is to where it stands now).
    const t = Math.max(0, Math.min(BREATH_ETA, s.eta ?? 9));
    if (!(s.eta >= 0 && s.eta <= BREATH_ETA)) continue;
    const p = vel ? { x: me.x + vel.x * t, z: me.z + vel.z * t } : me;
    const ax = p.x - s.position.x, az = p.z - s.position.z;
    const along = ax * s.heading.x + az * s.heading.z;
    if (along < -0.5 || along > (s.remaining || 0) + 0.5) continue;
    const side = Math.abs(ax * s.heading.z - az * s.heading.x);
    if (side < (s.radius || 0.5) + BREATH_BODY && (!hit || s.eta < hit.eta)) hit = s;
  }
  return { el: (now - st.t0) / 1000, facing: st.facing, gap, side: st.side, d, hit, jets: jets.length, reach };
}
// The dash out of a jet: 3.5-5.5 m, sideways round Infernus in `side`'s way by BREATH_GAIN deg at least, no nearer than
// BREATH_MELEE and no farther than now + 0.5 (or 6.5 m), with room, off the red. The most degrees gained (a little less
// for each metre beyond 6). null when none. Pure.
function breathDashCell(grid, me, C, side, areas = [], dNow = dist(me, C)) {
  if (!grid || !grid.reach) return null;
  const { origin, step, size, reach, clear } = grid;
  const wet = wetCells(grid), a0 = angDeg({ x: me.x - C.x, z: me.z - C.z });
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8 || clear[k] < 2 || wet(k)) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const md = dist(me, p);
    if (md < 3.5 || md > 5.5) continue;
    const dC = dist(p, C);
    if (dC < BREATH_MELEE || dC > Math.max(dNow + 0.5, 6.5)) continue;
    if (areas.some(a => !a.keepOut && areaDepth(p, a, 0.4) > 0)) continue;
    const gain = side * angDiff(angDeg({ x: p.x - C.x, z: p.z - C.z }), a0);
    if (gain < BREATH_GAIN) continue;
    const score = gain - 4 * Math.max(0, dC - 6) - 6 * Math.max(0, BREATH_R - 0.5 - dC) + 2 * Math.min(clear[k], 4);
    if (!best || score > best.score) best = { k, p, md, dC, gain, clear: clear[k], score };
  }
  return best;
}
// ----- end of iteration 33's breath part
// The breath's prefab numbers (spawns, spawnInterval, rotateSpeedRange; the jet's speed, endDistance, collisionRadiusRange,
// minHitInterval, dissipateOnTerrain) - read once a run while it breathes, logged, for tuning BREATH_*. Pure reads.
const breathCfg = { v: null, tries: 0 };
async function breathNumbers() {
  if (breathCfg.v || breathCfg.tries++ >= 3) return;
  const A = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_LavaLand_BossInfernus_BreathFire)', P = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_LavaLand_BossInfernus_BreathFire_Projectile)';
  const names = [[A, 'spawns'], [A, 'spawnInterval'], [A, 'rotateSpeedRange'], [A, 'endDaze'], [P, 'targetSpeed'], [P, 'initialSpeed'], [P, 'endDistance'],
    [P, 'collisionRadiusRange'], [P, 'minHitInterval'], [P, 'startInFrontDistance'], [P, 'dissipateOnTerrain'], [P, 'canCollideMidFlight']];
  const v = await Promise.all(names.map(([b, n]) => peek(b + '.' + n)));
  if (v.every(x => x === undefined || x === null)) return;
  breathCfg.v = Object.fromEntries(names.map(([, n], i) => [n, v[i]]));
  log(`  Infernus's breath: its numbers ${JSON.stringify(breathCfg.v)}`);
}

// ----- iteration 33: targets across the lava (pure) -----
// run-042's Room_LavaLand_Combat_0_1 (bot.log 262-281, 09:17:30-58, 26 of its 90 s): a Fire Elemental and a Scorching
// Wolf 16-25 m off across the lava; the target flipped between them with every look at who was nearest (chooseTarget:
// the nearest), plan() circled "closing in round it" - its cells are the 8 m round the hero, so it paced the lava's edge
// ((-62..-78, 57..61), 16 m to and fro) with 0 shots, and stepped onto the lava once (19 + 43 of burn).
// stickyTarget: the one being fought stays the target until another is STICKY_M nearer, or in reach while it is not, or
// on this side of the lava while it is across (`across(e)`: its /nav/path crosses the lava - shield.lavaWay); among the
// rest, the nearest on this side first. entities: the ones that can be hurt, nearest first. Returns an entity.
const STICKY_M = 3, LAVA_HOLD_MAX = 8000;
function stickyTarget(prevId, entities, me, range, across = () => false) {
  if (!entities.length) return null;
  const by = e => dist(me, e.position);
  const best = entities.filter(e => !across(e)).sort((x, y) => by(x) - by(y))[0] || entities.slice().sort((x, y) => by(x) - by(y))[0];
  const cur = prevId != null ? entities.find(e => e.id === prevId && e.alive !== false) : null;
  if (!cur || cur === best) return best;
  if (across(cur) && !across(best)) return best;
  if (by(best) <= range && by(cur) > range) return best;
  if (by(best) < by(cur) - STICKY_M) return best;
  return cur;
}
// ----- end of iteration 33's target part

// Skoll's Death From Above, its landing (iteration 18; run-025 158 + the stun it leaves). Decompiled
// (history/Ai_Mon_SnowMountain_BossSkoll_DeathFromAbove.cs, history/it18/..._Land.cs): Skoll rises
// (invulnerable), rains sword waves, then plays a follow telegraph (fxFollowTelegraph) at _followPos, which
// starts within followStartRandomMag of the hero and moves toward the hero at followSpeed for followDuration,
// Skoll teleported onto it every frame; then he descends there and lands: an InstantDamageInstance (the strike
// /threats lists only as it goes off, "lands in 0s") that stuns for 1.5 s. /threats reads the telegraph as an
// untimed blob of its renderer bounds (13.4-14.7 m, no caster, no type) - a "blob, no timer" that kept the
// last charge back (run-025: 1.9 s inside it, walking, the charge ready; dashed as the blow landed). Measured:
// the Land strike is 4.5 m (run-025, run-026); it lands 1.97 s after the drawing first shows (both runs); the
// drawing follows at ~3.5 m/s against the hero's ~4.4 on foot (run-025: 0.4 -> 1.4 m in 1.6 s), so no walk
// gets out of it - a dash does (5.5 m, then the gap grows). So the blob - no caster, no type, radius >= 12.5,
// centred within 3 m of Skoll - is read as a 4.5 m strike landing DFA_T s after it was first seen: the red
// the dash is for (areaUrgent at ~1 s left, a hard reason, the last charge too).
//   areas  readAreas' list (changed in place); skoll the boss entity; st { first, last } kept by the caller
// Returns the strike put in, or null.
const DFA_R = 4.5, DFA_T = 1.9, DFA_BLOB = 12.5;
function skollLanding(areas, skoll, st, nowMs) {
  if (!skoll) return null;
  const i = areas.findIndex(a => a.shape === 'blob' && !a.by && !a.type && a.radius >= DFA_BLOB && dist(a.centre, skoll.position) <= 3);
  if (i < 0) return null;
  if (!st.last || nowMs - st.last > 700) st.first = nowMs;
  st.last = nowMs;
  const age = (nowMs - st.first) / 1000;
  const left = Math.round(Math.max(0, DFA_T - age) * 100) / 100;
  const a = areas[i];
  areas[i] = { shape: 'strike', centre: a.centre, radius: DFA_R, inner: 0, angle: 360, fill: Math.min(1, age / DFA_T), left, by: 'Skoll', type: 'Skoll_DeathFromAbove_Landing', drawn: a.radius };
  return areas[i];
}

// Skoll's landing follows the hero (iteration 19; run-028's 158). history/Ai_Mon_SnowMountain_BossSkoll_DeathFromAbove.cs:
// for followDuration the landing point (_followPos) moves toward the nearest hero at followSpeed, then Skoll descends
// (descendTime) and lands there. run-028 (the mod's strike, probe.jsonl 06:16:56.0-57.9): the centre came on at ~3.9 m/s
// against the hero's ~4.9 and stopped ~0.16 s before the blow. The dash at 0.87 s left (areaUrgent, 3.6 m in) went to a
// cell 37 deg off straight-away (plan's dash cell) and 4.7 m long: 4.8 m from the centre after it (0.49 s left); then
// plan() walked across, not away (4.0-4.3 m to the end), and the blow landed at 4.5 m: 158 and the stun. Straight away,
// on foot at ~5 m/s against ~3.9, the gap grows; a 5.5 m dash straight away 0.7 s out leaves the follow ~2.1 m to make up.
// So near a following strike: no soft dashes, walk straight away from its centre (awayCell's cell, so walls count), and
// dash straight away (awayCell) once inside it with FOLLOW_DASH s or less left.
const FOLLOWS = /DeathFromAbove_Landing/, FOLLOW_NEAR = 2.5, FOLLOW_DASH = 0.7;
const followStrike = (areas, me) => areas.find(a => isStrike(a) && FOLLOWS.test(a.type || '') && a.left > 0 && dist(me, a.centre) < a.radius + FOLLOW_NEAR) || null;
function followMove(grid, me, fol, areas, canDash) {
  const fd = dist(me, fol.centre);
  const other = areas.filter(a => a !== fol);
  const cell = grid ? awayCell(grid, me, fol.centre, other) : null;
  if (fd < fol.radius + 0.4 && canDash && fol.left <= FOLLOW_DASH && cell && dist(cell.p, fol.centre) > fd + 3) return { dash: cell, fd };
  const to = cell && dist(cell.p, fol.centre) > fd ? cell.p : fd > 0.2 ? { x: me.x + (me.x - fol.centre.x) / fd, z: me.z + (me.z - fol.centre.z) / fd } : null;
  if (!to) return { fd };
  const td = dist(me, to);
  return { walk: { x: (to.x - me.x) / td, z: (to.z - me.z) / td }, fd };
}

// How fast the hero really walks (iteration 19). The escape from red (areaUrgent) reckoned 5 m/s on foot whatever
// happened: run-027 stood in Skoll's AuraBlade box 1.0 m from its edge with 0.93 s left, walked ("walkOut true":
// 1.0 m is 0.2 s), did not move - the hero had been rooted since 35.5 - and dashed at 0.14 s left (239, then 76:
// dead). Skoll's blows also leave Se_Elm_Cold (history/it19/Se_Elm_Cold.cs: a 35% slow a stack): 2 m/s in that
// trace. So the speed over the newest >= 0.3 s of looks in which a walk was in force (hist: { t, x, z, walking },
// kept by fight()), capped at HERO_WALK; null when there is no such stretch (standing by choice - the full speed is
// there when wanted). escapeUrgent: the old rule with that speed (null: 5 m/s, as before), never below WALK_MIN_V.
const HERO_WALK = 5, WALK_MIN_V = 1;
function walkSpeed(hist, now) {
  const n = hist.length;
  if (n < 2 || !hist[n - 1].walking || now - hist[n - 1].t > 250) return null;
  for (let j = n - 2; j >= 0; j--) {
    if (!hist[j].walking) return null;
    const dt = (hist[n - 1].t - hist[j].t) / 1000;
    if (dt >= 0.3) return Math.min(HERO_WALK, Math.hypot(hist[n - 1].x - hist[j].x, hist[n - 1].z - hist[j].z) / dt);
  }
  return null;
}
const escapeUrgent = (esc, v) => !!esc && esc.depth / Math.max(WALK_MIN_V, v == null ? HERO_WALK : v) + 0.2 > esc.left;

// Skills that hold the hero (iteration 19; run-027's death). A cast is the trigger's channel - TriggerChannelData:
// Move|Ability|Attack blocked for channel.duration unless the prefab says otherwise - then its postDelay, a daze
// (EntityControl.StartDaze: Everything blocked, the dash too) - history/AbilityTrigger.cs OnStartChannel and
// OnCastComplete. Both are prefab values: read once per skill type (skillLock: `#id.currentConfig.channel.duration`,
// `.channel.blockedActions`, `.postDelay`, pure reads). run-027: Doomsday Meteor (St_E_DoomsdayMeteor, from a
// Memory shrine in the room before Skoll) - after each of its three casts the hero stood 1.35, 1.75 and 2.4+ s
// (walks and dashes did nothing, "dash ready/2" before and after), Skoll 3 m off: 124, 124, then AuraBlade 239 + 76.
// lockOf: the seconds a cast holds the hero in place - the channel if it blocks moving, plus the daze.
const HEAVY_LOCK = 0.6, HEAVY_CLEAR = 7;
const KNOWN_HEAVY = new Set(['St_E_DoomsdayMeteor']);   // seen holding the hero, whatever the read says
const BLOCK = { None: 0, Move: 1, Ability: 2, Attack: 4, Dodge: 8, Cancelable: 0x80, Everything: 0xF, EverythingCancelable: 0x8F };
function blockFlags(v) {
  if (typeof v === 'number') return v;
  if (typeof v !== 'string') return null;
  let f = 0;
  for (const w of v.split(/[,|\s]+/).filter(Boolean)) { if (!(w in BLOCK)) return null; f |= BLOCK[w]; }
  return f;
}
function lockOf({ channel, blocks, post }) {
  const f = blockFlags(blocks);
  const ch = typeof channel === 'number' && channel > 0 ? channel : 0;
  return Math.round(((f === null || (f & BLOCK.Move) ? ch : 0) + (typeof post === 'number' && post > 0 ? post : 0)) * 100) / 100;
}

// /damage, read every ~0.4 s (fight, the loot's fire wait): the burning pools the hits come from
// (notePools), and what was taken since the last "took" line (tookAcc, logged every 3 s in fight).
const pools = [];
const bhHit = { first: 0, last: 0, at: null };   // Nyx's Blackhole hits in this room (blackholeNow)
// Blind bosses (dashChoice's `blind`): known ones by type, and any zone boss that lands a blow of
// BLIND_HIT of max health with the hero in no red in the 1.2 s before it (unseen) - not a shot's
// (Belphomet's missiles: 72 of 356; shots have their own hard dash reason). Reset per room.
// Iteration 22: Belphomet's SpawnMissiles (decompiled, history/it22): waves of straight missiles fanned evenly round it (3, 5, 7, ...
// a wave, a random start angle) - a dash off one line lands on the next; no "shot incoming" dash for them.
const BURST_SHOT = /SpawnMissiles_Missile/;
// The burning shots seen near the hero (noteFire: eta < 1.5 s, within 2 m of its line) over the last 2 s of looks, and on a hit
// one line (fireShotSay): how many looks saw a burning shot before it, the first and the last (eta / miss / distance), whether
// plan() was side-stepping, the last dash - enough to tell a shot never seen from one seen and not left. Pure but for seen.
const fireSeen = [];
function noteFire(seen, shots, me, dodging, now) {
  for (const s of shots || []) if (burnShot(s) && s.eta < 1.5 && s.miss < s.radius + 2) seen.push({ t: now, id: s.id, eta: s.eta, miss: s.miss, r: s.radius, speed: s.speed, dodging, d: s.position && me ? dist(me, s.position) : null });
  while (seen.length && now - seen[0].t > 2000) seen.shift();
}
function fireShotSay(seen, h, now) {
  const w = seen.filter(x => now - x.t < 1600), looks = new Set(w.map(x => x.t)).size;
  const f = w[0], l = w[w.length - 1], fmt = x => `eta ${x.eta}s miss ${x.miss}m (r ${x.r})${x.d != null ? ' ' + x.d.toFixed(1) + 'm off' : ''}`;
  const dash = seen.dashAt && now - seen.dashAt < 1600 ? `a dash (${seen.dashWhy}) ${((now - seen.dashAt) / 1000).toFixed(1)}s before` : 'no dash';
  return `  fire shot: ${Math.round(h.amount)} from ${h.by}${h.caster ? '/' + h.caster : ''} - ${looks ? `seen in ${looks} look${looks > 1 ? 's' : ''} before it (first ${fmt(f)}, last ${fmt(l)}), side-stepping in ${new Set(w.filter(x => x.dodging).map(x => x.t)).size}` : 'not seen near the hero in the 1.6 s before it'}; ${dash}`;
}
// The hard dash for a burning shot about to hit (eta < 0.3 s, within its radius + 0.5 of the hero - the body), in any fight, with a
// charge to spare (charges >= 2; unknown: yes). Pure.
const burnDash = (shots, charges) => (charges == null || charges >= 2) && (shots || []).some(s => burnShot(s) && s.eta < 0.3 && s.miss < s.radius + 0.5);

// ----- iteration 54: monsters' blows listed from their wind-up; pure -----
// The costliest blows not listed in time (runs 030-056, bot.log "took"): the Snow Wolf's Pounce 969 hp in 5 SnowMountain visits
// (038 183, 039 281, 048 59, 049 183, 056 263 - 16 hits of 55-122, never listed: a DashAttackInstance; most came right after a
// soft dash "on top of us" / "in a blob of ?" put the hero 4-5 m off, inside its 6.5 m cast range), Big Baam's beam 476 (034 171,
// 040 153, 050 152: its box listed only as the beam began - "dash: in a box of Big Baam (0.1m in, lands in 0s)"), the Soul
// Swordsman's SwiftStep 446 (the slash 208 once, its shots 51-102 four times: it lands 1.5 m behind the hero, slashes round its
// front 1.2 s / attack speed later and shoots along the way to the hero - where the hero leaves the slash). Decompiled
// (history/it54) and read out of the bundle (tools/bundle.mjs `go` / `tree`): the Pounce - a 0.65 s channel, cast within 6.5 m,
// then a 5.5 m dash in 0.4 s (x1.5 as a miniboss) sweeping a 1.25 x 2.25 m box 1.5 m ahead: a lane ~8.1 m long, 1.25 m wide;
// the beam - a 0.55 s channel, then a tip growing from 1 to 15 m (eased in) over 0.8 s x attack speed from 1.3 m ahead, hurting
// 0.35 m round it (x3, 15 deg apart, as a miniboss); SwiftStep - a 0.3 s channel, 0.15 s unseen to 1.5 m behind the hero, the
// slash a 4 m pie (10 points) round its front, its shots (11 m) along that way.
// The mod lists them from their wind-up (readers 54, proposals/iter-54-mod.md): the red then goes through plan()'s cells, the
// sweep rule (a lane: the step goes across it), the escape (a walk while there is time, a hard dash when not), the soft dash's
// landing and the Precision Shot hold - nothing new is needed for that. Here: the lanes widened by the hero's body (laneReach -
// the beam's sphere cast and the pounce's box take the body, the box is the collider only), and one line when each is listed
// (watchSay) and on each hit (watchHitSay: listed or not, how early, a dash or not) - what the next run's log has to show.
const LANE_TYPES = /SnowWolf_Pounce|BigBaam_BeamAtk|GhostBlade_SwiftStep_Projectile/;
const LANE_REACH = 0.35;
function laneReach(areas) {
  for (let i = 0; i < areas.length; i++) {
    const a = areas[i];
    if (!a || a.shape !== 'box' || a.reach || !Array.isArray(a.corners) || a.corners.length !== 4 || !LANE_TYPES.test(a.type || '')) continue;
    const c = a.corners, w = dist(c[0], c[1]) || 1, u = { x: (c[1].x - c[0].x) / w, z: (c[1].z - c[0].z) / w };
    const mv = (p, s) => ({ ...p, x: Math.round((p.x + u.x * s) * 1000) / 1000, z: Math.round((p.z + u.z * s) * 1000) / 1000 });
    areas[i] = { ...a, corners: [mv(c[0], -LANE_REACH), mv(c[1], LANE_REACH), mv(c[2], LANE_REACH), mv(c[3], -LANE_REACH)], reach: LANE_REACH };
  }
  return areas;
}
const WATCH = [
  { k: 'pounce', re: /SnowWolf_Pounce/, name: "the Snow Wolf's Pounce" },
  { k: 'beam', re: /BigBaam_BeamAtk/, name: "Big Baam's beam" },
  { k: 'swiftstep', re: /GhostBlade_SwiftStep/, name: "the Soul Swordsman's SwiftStep" },
  { k: 'claw', re: /BossSeeker_TunnelVision_Claw/, name: "the Seeker's Claw" },
  { k: 'rageblade', re: /BossDarkMoon_Blade_RageInstance/, name: "a hallucination's Blade" },
];
const watchOf = type => WATCH.find(w => w.re.test(type || '')) || null;
const watchSeen = [], watchSaid = new Map();
let watchReaders = null;
// Each look: the watched areas, how deep the hero is in each (0 = clear), kept 2.5 s.
function watchNote(seen, areas, me, now) {
  for (const a of areas || []) {
    const w = a && watchOf(a.type);
    if (!w || !a.centre) continue;
    seen.push({ t: now, k: w.k, type: a.type, by: a.by || '?', shape: a.shape, left: a.left, d: areaDepth(me, a, 0), windup: /^Windup_/.test(a.type) });
  }
  while (seen.length && now - seen[0].t > 2500) seen.shift();
}
// One line a cast: a watched kind from one caster seen again after WATCH_GAP ms without it.
const WATCH_GAP = 1500;
function watchSay(said, areas, me, now) {
  const out = [], seenNow = new Set();
  for (const a of areas || []) {
    const w = a && watchOf(a.type);
    if (!w || !a.centre) continue;
    const key = w.k + '|' + (a.by || '?');
    if (seenNow.has(key)) continue;
    seenNow.add(key);
    const prev = said.get(key);
    said.set(key, now);
    if (prev != null && now - prev <= WATCH_GAP) continue;
    const d = areaDepth(me, a, 0), n = (areas || []).filter(b => b && b.type === a.type && (b.by || '?') === (a.by || '?')).length;
    out.push(`  listed: ${w.name} (${a.by || '?'}) ${/^Windup_/.test(a.type) ? 'from its wind-up' : 'under way'} - ${n > 1 ? n + ' ' : 'a '}${a.shape}${n > 1 ? 's' : ''}${a.radius ? ' of ' + a.radius + ' m' : ''}, ${a.left}s left, the hero ${d > 0 ? d.toFixed(1) + 'm in it' : dist(me, a.centre).toFixed(1) + 'm from its centre, clear'}`);
  }
  return out;
}
// On a hit from a watched kind: was it listed in the 2 s before it (the first and last look: time left, the hero's depth), and a dash.
function watchHitSay(seen, h, now, dash = {}, readers = null) {
  const w = watchOf(h && h.by);
  if (!w) return null;
  const s = seen.filter(x => x.k === w.k && now - x.t < 2000 && now >= x.t), looks = new Set(s.map(x => x.t)).size;
  const fmt = x => `${x.left}s left, ${x.d > 0 ? `the hero ${x.d.toFixed(1)}m in it` : 'the hero clear'}${x.windup ? ' (wind-up)' : ''}`;
  const dashTxt = dash.at && now - dash.at < 2000 ? `a dash (${dash.why}) ${((now - dash.at) / 1000).toFixed(1)}s before` : 'no dash';
  return `  listed hit: ${Math.round(h.amount)} from ${w.name}${h.caster ? ' (' + h.caster + ')' : ''} - ${looks ? `listed in ${looks} look${looks > 1 ? 's' : ''} before it (first ${((now - s[0].t) / 1000).toFixed(1)}s before: ${fmt(s[0])}; last: ${fmt(s[s.length - 1])})` : `not listed in the 2 s before it (readers ${readers ?? '?'})`}; ${dashTxt}`;
}
// ----- end of iteration 54's pure part
const BLIND_BOSSES = /BossDarkMoon/, BLIND_HIT = 0.2, SHOT_HIT = /Projectile|Missile|Arrow|Orb|Bullet|Shot|Fireball/i;   // iteration 46: + Doom's fireballs
const blind = { hits: 0, redAt: [], queue: [] };
// chooseTarget's state in a fight: the far look (/entities within 300 m, at most once a second, while the
// boss is invulnerable - a pillar may stand beyond the 35 m the fight reads), what was last said, and
// whether the way to a far target crosses lava (/nav/path, once per target).
const shield = { far: [], farAt: 0, said: null, targetId: null, since: 0, lavaWay: new Map(), lavaAt: new Map() };
let tookAcc = {};
const lavaHit = { at: 0, total: 0, recent: [] };   // the last LavaLand_Lava hit read (readHits), all of them summed, the last 2 s of them
let hitTotal = 0;   // every hit read from /damage, summed (the unexplained-loss check in fight)

// One read, no retries (get() retries an error 3 times, 300 ms apart - a read of something that is
// not there is an error): undefined when it fails.
const peek = async path => { try { return await call('GET', '/reflect/get?' + new URLSearchParams({ path })); } catch { return undefined; } };
// Iteration 26: whether the zone boss can be hurt now, by the game's own check - pure getters on its EntityStatus:
// hasDamageImmunity (Invulnerable or Protected: what Actor's DealDamage throws a hit away on) and hasUntargetable, every
// IMM_READ ms; while either holds, its status effects' types once a second (depth 1: each one as a {type} ref) to name the
// phase. The mod's fields (/entities `immune`, `untargetable`, `effects` - proposals/iter-26-mod.md) replace the reads when
// present. Five failed reads in a row: no more reads this fight (the flag alone, as before).
const IMM_READ = 250;
async function readImmunity(boss, imm, now = Date.now()) {
  if (imm.id !== boss.id) Object.assign(imm, { id: boss.id, readAt: 0, immune: null, untargetable: null, effects: null, fxAt: 0, fails: 0 });
  if ('immune' in boss) {
    imm.immune = boss.immune === true; imm.untargetable = boss.untargetable === true; imm.via = 'mod';
    if (Array.isArray(boss.effects)) imm.effects = boss.effects.map(x => typeof x === 'string' ? x : x && (x.type || x.$type)).filter(Boolean);
    return;
  }
  if (imm.fails >= 5 || (imm.readAt && now - imm.readAt < IMM_READ)) return;
  imm.readAt = now;
  const [a, b] = await Promise.all([peek(`#${boss.id}.Status.hasDamageImmunity`), peek(`#${boss.id}.Status.hasUntargetable`)]);
  if (typeof a !== 'boolean') {
    if (++imm.fails >= 5) log(`  boss immunity: the reads of #${boss.id}.Status failed 5 times - the invulnerable flag alone from now on`);
    return;
  }
  imm.fails = 0; imm.immune = a; imm.untargetable = b === true; imm.via = 'read';
  if ((a || b === true) && (!imm.fxAt || now - imm.fxAt > 1000)) {
    imm.fxAt = now;
    let l;
    try { l = await call('GET', '/reflect/get?' + new URLSearchParams({ path: `#${boss.id}.Status.statusEffects`, depth: '1' })); } catch { l = undefined; }
    if (Array.isArray(l)) imm.effects = l.map(x => x && (x.type || x.$type)).filter(Boolean);
  }
}
// Nyx's Blackhole by pure reads of the ability, polled in the Nyx fight (blackholeNow): 'on',
// 'coming' (created, not on yet: Nyx flying to the centre - its start time is still 0), or null (none,
// or over). The first time one is found its numbers are logged once (prefab values the code does not
// show: how far it hurts and pulls, how long it lasts). Where Nyx flies to: Sky_BossRoomCenter.
const BH_PATH = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_Sky_BossNyx_Blackhole)';
const bh = { readAt: 0, polled: null, cfg: null, centre: undefined, said: null };
async function bossBlackhole() {
  const [on, start] = await Promise.all([peek(BH_PATH + '.Network_isBlackholeOn'), peek(BH_PATH + '.Network_blackholeStartNetworkTime')]);
  bh.polled = on === true ? 'on' : on === false && start === 0 ? 'coming' : null;
  if (bh.polled && !bh.cfg) {
    const names = ['tickDamageRadius', 'explodeDamageRadius', 'displaceDuration', 'blackholeDuration', 'afterBlackholeDelay', 'tickInterval', 'tickDamageRatio', 'explodeDamageRatio', 'distanceBounds'];
    const vals = await Promise.all(names.map(n => peek(BH_PATH + '.' + n)));
    bh.cfg = Object.fromEntries(names.map((n, i) => [n, vals[i]]));
    log(`  blackhole: its numbers ${JSON.stringify(bh.cfg)}`);
    // Its curves (prefab data the code does not show), sampled by AnimationCurve.Evaluate - a pure
    // getter: the pull in m/s by distance (0 = distanceBounds.x, 1 = .y), its multiplier over its life
    // (0..1 of blackholeDuration), and the tick damage's. pullAt() uses the first two.
    const xs = [0, 0.1, 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 1];
    const curve = async name => {
      const v = await Promise.all(xs.map(x => peek(`${BH_PATH}.${name}.Evaluate(${x})`)));
      return v.every(y => typeof y === 'number') ? v.map(y => Math.round(y * 100) / 100) : null;
    };
    // Not waited for: the fight goes on while they come.
    Promise.all(['attractStrengthByDist', 'attractStrengthMulOverLifetime', 'dmgMultiplierOverLifetime'].map(curve)).then(([byDist, overLife, dmgLife]) => {
      bh.curves = byDist && overLife ? { byDist, overLife } : null;
      log(`  blackhole: its curves at 0, 0.1 .. 1 - pull by distance ${JSON.stringify(byDist)}, x over its life ${JSON.stringify(overLife)}, damage x over its life ${JSON.stringify(dmgLife)}`);
    }).catch(() => { });
  }
}
// Iteration 28: a lobbed shot's blast radius (lobbedAreas): its `range` collider, read once per type by pure getters on the
// shot itself (#netId) - the radius times the collider's scale. Not waited for; LOB_R until it comes.
const lobReading = new Set();
function lobRadius(shot) {
  if (!shot || shot.id == null || lobR.has(shot.type) || lobReading.has(shot.type)) return;
  lobReading.add(shot.type);
  Promise.all([peek(`#${shot.id}.range.radius`), peek(`#${shot.id}.range.transform.lossyScale`)]).then(([r, sc]) => {
    const k = sc && typeof sc.x === 'number' && sc.x > 0 ? sc.x : 1;
    if (typeof r === 'number' && r > 0.1) { lobR.set(shot.type, Math.round(r * k * 100) / 100); log(`  lobbed: ${shot.type} blows up ${lobR.get(shot.type)} m round where it lands (its range, read)`); }
    else lobReading.delete(shot.type);   // gone before the read: the next one is read
  }).catch(() => lobReading.delete(shot.type));
}
// Iteration 28 (run-040's Nyx: 2 x 403, 40% of max hp each, at 7.9 and 6.9 m from the room's centre, "blind boss" both
// times - /threats lists nothing for it). Decompiled (history/it26/Se_Mon_Sky_BossNyx_PhaseChange.cs): at each phase
// Nyx is dazed for staggerDuration, flies to Sky_BossRoomCenter (displaceToCenter) and after prepareExplodeDuration
// hurts everyone in explodeRange round her. Read by pure getters while she cannot be hurt (the status effect, its numbers
// once, the game's clock): a strike at the room's centre (her position without displaceToCenter), landing at
// creationTime + staggerDuration + prepareExplodeDuration; PC_R until the range is read. Skipped when /threats lists it.
const PC_PATH = 'UnityEngine.Object.FindObjectOfType(Se_Mon_Sky_BossNyx_PhaseChange)';
const PC_R = 9, PC_TYPE = 'Se_Mon_Sky_BossNyx_PhaseChange';
const nyxPhase = { readAt: 0, cfg: null, area: null, said: 0 };
async function nyxPhaseRead(nyx, centre) {
  const [created, now] = await Promise.all([peek(PC_PATH + '.creationTime'), peek('UnityEngine.Time.time')]);
  if (typeof created !== 'number' || typeof now !== 'number') { nyxPhase.area = null; return; }
  if (!nyxPhase.cfg) {
    const names = ['staggerDuration', 'prepareExplodeDuration', 'displaceToCenter', 'displaceDuration', 'afterExplodeDuration', 'explodeRange.radius', 'explodeRange.transform.lossyScale'];
    const v = await Promise.all(names.map(n => peek(PC_PATH + '.' + n)));
    const cfg = Object.fromEntries(names.map((n, i) => [n, v[i]]));
    const k = cfg['explodeRange.transform.lossyScale'] && typeof cfg['explodeRange.transform.lossyScale'].x === 'number' ? cfg['explodeRange.transform.lossyScale'].x : 1;
    cfg.r = typeof cfg['explodeRange.radius'] === 'number' && cfg['explodeRange.radius'] > 0.1 ? Math.round(cfg['explodeRange.radius'] * k * 100) / 100 : null;
    if (typeof cfg.staggerDuration !== 'number' || typeof cfg.prepareExplodeDuration !== 'number') return;
    nyxPhase.cfg = cfg;
    log(`  Nyx's phase change: its numbers ${JSON.stringify({ stagger: cfg.staggerDuration, prepare: cfg.prepareExplodeDuration, toCentre: cfg.displaceToCenter, displace: cfg.displaceDuration, after: cfg.afterExplodeDuration, radius: cfg.r })}`);
  }
  const c = nyxPhase.cfg, at = c.staggerDuration + c.prepareExplodeDuration, left = at - (now - created);
  if (left < -0.1) { nyxPhase.area = null; return; }
  const where = c.displaceToCenter !== false && centre ? centre : nyx && nyx.position;
  if (!where) { nyxPhase.area = null; return; }
  nyxPhase.area = { shape: 'strike', centre: { x: where.x, z: where.z }, radius: c.r || PC_R, inner: 0, angle: 360, fill: Math.round(Math.max(0, Math.min(1, 1 - left / Math.max(0.1, at))) * 100) / 100,
    left: Math.round(Math.max(0, left) * 100) / 100, by: 'Nyx', type: PC_TYPE, created };
}
// Iteration 28 (run-041's death, boss-trace 09:07:43.1-43.6: 321 -> 122 -> 81 -> 37 in 0.5 s, 224 + 41 + 43 + 43 of
// Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave at 7.3-7.9 m from White Night, "0 red areas", a charge ready). Decompiled
// (history/it28/Ai_Mon_Ink_BossWhiteNight_DestructionWave*.cs): she dashes past the hero (DestructionWave, backDistance),
// turns to it, then the Wave: after startDelay + 0.1 its box `range` - moved onto her every frame, turned with her - hurts
// everyone in it every `interval` (firstDmg, then dmgPerAtk; knockback along her facing) while she turns maxRotationAngle
// (signed: toward the side the hero stood on) over rotationDuration, eased in and out, from rotationDelay on; rage: the box
// x1.5 wide x2 long, the turn doubled. waveSlice: the slice still to be swept - centre her, radius the box's far corner,
// from where she faces now through the rest of her turn plus the box's width; left = until it starts hurting (0 while it
// does). Pure.
function waveSlice(c, age, fwdNow, at) {
  if (!c || !fwdNow || !at) return null;
  const on = c.startDelay + 0.1, turnAt = on + c.rotationDelay, end = turnAt + c.rotationDuration;
  if (age > end + 0.1) return null;
  const far = (c.offY + c.sizeY * 0.5) * c.sz, halfW = (Math.abs(c.offX) + c.sizeX * 0.5) * c.sx;
  const rad = Math.hypot(far, halfW);
  const t = c.rotationDuration > 0.01 ? Math.max(0, Math.min(1, (age - turnAt) / c.rotationDuration)) : 1;
  const eased = t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) * (-2 * t + 2) / 2;
  const rest = c.maxRotationAngle * (1 - eased);   // degrees still to turn, signed as Quaternion.AngleAxis about +Y
  const width = far > 0.1 ? 2 * Math.atan2(halfW, far) * 180 / Math.PI : 180;
  const angle = Math.min(360, Math.abs(rest) + width);
  const fl = Math.hypot(fwdNow.x, fwdNow.z) || 1, fx = fwdNow.x / fl, fz = fwdNow.z / fl;
  const th = rest / 2 * Math.PI / 180;   // +Y rotation: +Z toward +X
  const facing = { x: Math.round((fx * Math.cos(th) + fz * Math.sin(th)) * 1000) / 1000, z: Math.round((-fx * Math.sin(th) + fz * Math.cos(th)) * 1000) / 1000 };
  return { shape: angle >= 359 ? 'circle' : 'slice', centre: { x: at.x, z: at.z }, radius: Math.round(rad * 100) / 100, inner: 0, angle: Math.round(angle * 10) / 10, facing,
    fill: Math.round(Math.max(0, Math.min(1, age / Math.max(0.1, end))) * 100) / 100, left: Math.round(Math.max(0, on - age) * 100) / 100, by: 'White Night', type: WAVE_TYPE };
}
// Iteration 49: the Wave's `range` is a POLYGON, not a box - waveSlice read its unused `size` (1 x 1): "a 270 deg slice of
// 0.71 m round her" while it hit at 7.3-8.0 m (run-041 224 + 41 + 43 + 43, dead; run-051 256 at 8.0 m). Its points, read
// out of the game's asset bundle (the Wave prefab's DewCollider: shape 2 = Polygon, points (x right, y forward, m)):
// a spike from 0.59 m behind her, 4 m wide at 0.68 m, 5 m wide at 2.15 m, to a point 17.5 m ahead. The prefab's numbers:
// interval 0.15, postDelay 1.5, startDelay 1.2, rotationDelay 0.5, rotationDuration 1.2, maxRotationAngle 180 (the sign
// set at the Wave's start - read live); rage: startDelay 1.5, rotationDuration 4, the turn doubled, the spike x1.5 wide x2
// long (35 m). Her dash before it (Ai_Mon_Ink_BossWhiteNight_DestructionWave): 0.5 s, unseen, to 2 m past where the hero
// stood; on landing she turns to the hero and the Wave starts: the spike on from startDelay + 0.1 (1.3 s) pointing at the
// hero, still for rotationDelay, then turning the whole maxRotationAngle - AWAY from the side the hero stood on (the sign
// flips when the hero is to her right) - eased in and out over rotationDuration. So the hero, on the spike's axis when it
// starts, gets out sideways to the side the turn leaves behind (~3 m at 2 m from her) within 1.3 s, and stays out of the
// half-circle the spike sweeps.
const WAVE_POLY = [{ x: 2, y: 0.68 }, { x: 2.52, y: 2.15 }, { x: 0, y: 17.5 }, { x: -2.52, y: 2.15 }, { x: -2, y: 0.68 }, { x: 0, y: -0.59 }];
const WAVE_END_T = 30;   // the spike's end place counts from when <= this many degrees of the turn are left
const WAVE_READERS = 49;   // the mod's reader of the Wave's polygon (proposals/iter-49-mod.md); older builds list a 0.71 m slice
// Turn a unit facing by deg about +Y (Unity: +Z toward +X). Pure.
const turnBy = (f, deg) => { const th = deg * Math.PI / 180; return { x: f.x * Math.cos(th) + f.z * Math.sin(th), z: -f.x * Math.sin(th) + f.z * Math.cos(th) }; };
// The polygon placed at `at` facing f (unit), scaled sx (across) / sz (along). Pure.
const wavePolyAt = (pts, at, f, sx = 1, sz = 1) => pts.map(p => ({ x: Math.round((at.x + f.z * p.x * sx + f.x * p.y * sz) * 100) / 100, z: Math.round((at.z - f.x * p.x * sx + f.z * p.y * sz) * 100) / 100 }));
// waveAreas: the red of a Wave whose `range` is a polygon (c.points; else [waveSlice] as before), as up to three areas of
// WAVE_TYPE: the spike where it is now (`poly`, left = until it starts hurting), the fan it sweeps through the rest of the
// turn (`poly`: her place + an arc of the spike's length every <= 10 deg; a `circle` past a full turn; left = until the turn
// starts), and the spike where the turn ends (`poly`, left = until <= WAVE_END_T deg are left). age: s since the Wave began;
// fwdNow: her facing now; at: her place. Null/[] when over. Pure.
function waveAreas(c, age, fwdNow, at) {
  if (!c || !fwdNow || !at) return [];
  if (!Array.isArray(c.points) || c.points.length < 3) { const s = waveSlice(c, age, fwdNow, at); return s ? [s] : []; }
  const on = c.startDelay + 0.1, turnAt = on + c.rotationDelay, dur = c.rotationDuration, end = turnAt + dur;
  if (age > end + 0.1) return [];
  const sx = c.sx || 1, sz = c.sz || 1;
  const fl = Math.hypot(fwdNow.x, fwdNow.z) || 1, f = { x: fwdNow.x / fl, z: fwdNow.z / fl };
  const ease = t => t < 0.5 ? 2 * t * t : 1 - (-2 * t + 2) * (-2 * t + 2) / 2;
  const t = dur > 0.01 ? Math.max(0, Math.min(1, (age - turnAt) / dur)) : 1;
  const rest = (c.turns === false ? 0 : c.maxRotationAngle) * (1 - ease(t));   // signed degrees still to turn
  const far = Math.max(...c.points.map(p => Math.hypot(p.x * sx, p.y * sz)));
  const fill = Math.round(Math.max(0, Math.min(1, age / Math.max(0.1, end))) * 100) / 100;
  const r2 = v => Math.round(Math.max(0, v) * 100) / 100;
  const base = { inner: 0, fill, by: 'White Night', type: WAVE_TYPE, wave: true };
  const polyArea = (pts, fw, left, part) => {
    const n = pts.length, cen = { x: pts.reduce((s, p) => s + p.x, 0) / n, z: pts.reduce((s, p) => s + p.z, 0) / n };
    return { ...base, shape: 'poly', corners: pts, centre: { x: Math.round(cen.x * 100) / 100, z: Math.round(cen.z * 100) / 100 },
      radius: Math.round(Math.max(...pts.map(p => Math.hypot(p.x - cen.x, p.z - cen.z))) * 100) / 100, facing: { x: Math.round(fw.x * 1000) / 1000, z: Math.round(fw.z * 1000) / 1000 }, left: r2(left), part };
  };
  const out = [polyArea(wavePolyAt(c.points, at, f, sx, sz), f, on - age, 'now')];
  if (Math.abs(rest) > 1) {
    const turnLeft = turnAt - age;
    if (Math.abs(rest) >= 359) out.push({ ...base, shape: 'circle', centre: { x: at.x, z: at.z }, radius: Math.round(far * 100) / 100, angle: 360, left: r2(turnLeft), part: 'fan' });
    else {
      const n = Math.max(2, Math.ceil(Math.abs(rest) / 10)), fan = [{ x: at.x, z: at.z }];
      for (let k = 0; k <= n; k++) { const d = turnBy(f, rest * k / n); fan.push({ x: Math.round((at.x + d.x * far) * 100) / 100, z: Math.round((at.z + d.z * far) * 100) / 100 }); }
      out.push(polyArea(fan, turnBy(f, rest / 2), turnLeft, 'fan'));
    }
    // When no more than WAVE_END_T deg are left: eased(t) = 1 - WAVE_END_T / |max| (t >= 0.5 for any turn over 60 deg).
    const need = Math.abs(c.maxRotationAngle) > WAVE_END_T ? 1 - WAVE_END_T / Math.abs(c.maxRotationAngle) : 0;
    const tEnd = need <= 0 ? 0 : need < 0.5 ? Math.sqrt(need / 2) : 1 - Math.sqrt(2 * (1 - need)) / 2;
    const fe = turnBy(f, rest);
    out.push(polyArea(wavePolyAt(c.points, at, fe, sx, sz), fe, turnAt + tEnd * dur - age, 'end'));
  }
  return out;
}
// Which side of her the turn sweeps, for the log: 'right' (clockwise from above, +Y) or 'left'. Pure.
const waveSide = c => c && c.maxRotationAngle > 0 ? 'right' : 'left';
// One line on a Wave's red (waveAreas' or the mod's - its three in the same order: now, fan, end): the spike's length, the
// turn's side, when it hurts, where the hero stands. at: her place; from: 'read' / 'the mod'. Pure.
function waveSay(wa, me, at, from) {
  if (!wa || !wa.length) return '';
  const now = wa.find(a => a.part === 'now') || wa[0];
  const fan = wa.find(a => a.part === 'fan') || (wa.length >= 3 ? wa[1] : null);
  const endA = wa.find(a => a.part === 'end') || (wa.length >= 3 ? wa[2] : null);
  if (now.shape !== 'poly') return `White Night's Destruction Wave (${from}): a ${now.angle} deg ${now.shape} of ${now.radius} m round her, hurting in ${now.left}s - the hero ${dist(me, now.centre).toFixed(1)}m from her${areaDepth(me, now, 0) > 0 ? ', in it' : ''}`;
  const len = Math.max(...now.corners.map(p => dist(p, at)));
  let turn = 'no turn';
  if (fan && fan.shape === 'circle') turn = 'turning a full circle';
  else if (fan && fan.facing && now.facing) {
    const cr = now.facing.x * fan.facing.z - now.facing.z * fan.facing.x;   // < 0: clockwise from above (+Y), her right
    const deg = Math.acos(Math.max(-1, Math.min(1, now.facing.x * fan.facing.x + now.facing.z * fan.facing.z))) * 360 / Math.PI;
    turn = `turning ~${Math.round(deg)} deg to her ${cr < 0 ? 'right' : 'left'}`;
  }
  const where = areaDepth(me, now, 0) > 0 ? 'in the spike' : fan && areaDepth(me, fan, 0) > 0 ? 'in its sweep' : endA && areaDepth(me, endA, 0) > 0 ? 'where it ends' : 'clear of it';
  return `White Night's Destruction Wave (${from}): a ${len.toFixed(1)} m spike, ${turn}, hurting in ${now.left}s - the hero ${dist(me, at).toFixed(1)}m from her, ${where}`;
}
// Read by pure getters while White Night is in sight (every 250 ms): the Wave (FindObjectOfType), its numbers once per Wave,
// the game's clock and her facing. Skipped when /threats lists it (the mod's readers >= WAVE_READERS; an older build's 0.71 m
// slice is dropped - fight()).
const WAVE_PATH = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave)';
const WAVE_TYPE = 'Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave';
const wave = { readAt: 0, created: null, cfg: null, area: null, areas: [], said: null };
async function waveRead(wn) {
  const [created, now, fwd] = await Promise.all([peek(WAVE_PATH + '.creationTime'), peek('UnityEngine.Time.time'), peek(`#${wn.id}.transform.forward`)]);
  if (typeof created !== 'number' || typeof now !== 'number' || !fwd || typeof fwd.x !== 'number') { wave.area = null; wave.areas = []; return; }
  if (wave.created !== created || !wave.cfg) {
    const names = ['startDelay', 'rotationDelay', 'rotationDuration', 'maxRotationAngle', 'interval', 'range.size', 'range.offset', 'range.transform.lossyScale', 'range.shape', 'range.points'];
    const v = await Promise.all(names.map(n => peek(WAVE_PATH + '.' + n)));
    const [startDelay, rotationDelay, rotationDuration, maxRotationAngle, interval, size, off, sc, shape, pts] = v;
    if (![startDelay, rotationDelay, rotationDuration, maxRotationAngle].every(x => typeof x === 'number')) { wave.area = null; wave.areas = []; return; }
    // The polygon as read (points {x, y}), else the prefab's (WAVE_POLY) unless the read says it is a box or circle.
    const read = Array.isArray(pts) && pts.length >= 3 && pts.every(p => p && typeof p.x === 'number' && typeof p.y === 'number') ? pts.map(p => ({ x: p.x, y: p.y })) : null;
    const points = shape === 'Box' || shape === 'Circle' ? null : read || WAVE_POLY;
    if (!points && (!size || typeof size.x !== 'number')) { wave.area = null; wave.areas = []; return; }
    wave.created = created;
    wave.cfg = { startDelay, rotationDelay, rotationDuration, maxRotationAngle, interval, sizeX: size && size.x, sizeY: size && size.y, offX: off && typeof off.x === 'number' ? off.x : 0,
      offY: off && typeof off.y === 'number' ? off.y : 0, sx: sc && typeof sc.x === 'number' ? sc.x : 1, sz: sc && typeof sc.z === 'number' ? sc.z : 1,
      shape: shape ?? '?', points, pointsFrom: points ? (read ? 'read' : 'prefab') : 'none' };
    log(`  White Night's Destruction Wave: its numbers ${JSON.stringify({ ...wave.cfg, points: undefined })}`);
  }
  wave.areas = waveAreas(wave.cfg, now - created, fwd, wn.position);
  wave.area = wave.areas[0] || null;
}
// ----- iteration 51: Dark Moon's Blade as it is -----
// Dark Moon's Blade cost 187-442 hp in every one of her fights since run-038: 10 hits, 2222 hp (runs 016-054), each "blind
// boss: ... with no red under the hero before it". Decompiled (history/it51/Ai_Mon_Ink_BossDarkMoon_Blade*.cs) and read out
// of the game's bundle (tools/bundle.mjs): the Blade is her ATTACK (At_..._Blade is an AttackTrigger). She dashes, unseen
// (renderers off, 100 m/s), to BLADE_SHORT (3.5) m short of the hero's place - at least BLADE_MIN_DASH (4) m, so from
// under 7.5 m she lands 0-3.5 m from the hero - then channels atkPrepareDuration 0.85 s (rage 0.75) / her attack speed,
// turned at once to where the hero will be then (PredictAngle), and the blow lands in her Blade_Instance's `range`, placed
// where she stands, facing that way: a POLYGON of 21 points (BLADE_POLY: x right, y forward, m) - a crescent round her
// front and sides, from 2.3 m (in front; ~4 m at the sides) out to 6.1-6.3 m, ~120 deg to each side. /threats never
// listed it (its reader dropped polygons of anyone but Primus). The hits came at 6.7 m (run-046), 6.9 (052) and 7.0 m
// (054) from her in front, where the outline is at 6.12 m: the blow takes the hero's body (and her place a tick off), so
// the crescent is grown BLADE_REACH (1.0) m all round (growPoly) - its hollow near her shrinks the same. Every one of
// those three: she landed 2.9-4.1 m off, the "boss close" dash took the hero straight away to 6.6-7.0 m and it circled
// there. The way out: past ~7.5 m (a dash from 3.5 m), or behind her.
const BLADE_POLY = [{ x: -6.28, y: 1.54 }, { x: -6.08, y: 3.15 }, { x: -4.74, y: 4.75 }, { x: -2.71, y: 5.8 }, { x: -0.09, y: 6.12 }, { x: 2.62, y: 5.78 },
  { x: 4.85, y: 4.63 }, { x: 6.07, y: 3.01 }, { x: 6.29, y: 1.26 }, { x: 5.93, y: 0.08 }, { x: 5.01, y: -0.95 }, { x: 3.72, y: -2.14 }, { x: 4.32, y: 0.06 },
  { x: 3.29, y: 1.29 }, { x: 2.07, y: 2 }, { x: -0.04, y: 2.32 }, { x: -2.03, y: 2.14 }, { x: -3.41, y: 1.17 }, { x: -3.93, y: 0.09 }, { x: -3.46, y: -2.04 },
  { x: -5.52, y: -0.26 }];
// The red kept out of is that crescent filled in (BLADE_SOLID: the outer edge, 6.1 m ahead, 6.8 m at 60 deg, then round her back
// 0.5 m behind her): its hollow (2.3 m ahead) is no way out - she stands in it and her knockback throws the hero outward, and
// the way to its nearest edge from 3 m in front would read "inward, 2 m" instead of "out, 4 m".
const BLADE_SOLID = [...BLADE_POLY.slice(0, 12), { x: 1.5, y: -0.5 }, { x: -1.5, y: -0.5 }, ...BLADE_POLY.slice(19)];
const BLADE_TYPE = 'Ai_Mon_Ink_BossDarkMoon_Blade';
// The mod's reader of it (proposals/iter-51-mod.md) lists a `poly` typed BLADE_TYPE; builds before it list nothing. The bot goes by
// the listing itself, not by /threats' `readers` (other iterations' patches raise that number on their own).
const BLADE_REACH = 1.0, BLADE_PREP = 0.85, BLADE_SHORT = 3.5, BLADE_MIN_DASH = 4;
// A polygon (corners {x, z}) grown d m outward: each corner moved along the bisector of its edges' outward normals by
// d / cos (the miter, at most 2.5 d) - a concave corner moves inward the same way. Either winding. Pure.
function growPoly(pts, d) {
  const n = pts.length;
  if (n < 3 || !(d > 0)) return pts.map(p => ({ x: p.x, z: p.z }));
  let a2 = 0;
  for (let i = 0; i < n; i++) { const p = pts[i], q = pts[(i + 1) % n]; a2 += p.x * q.z - q.x * p.z; }
  const s = a2 >= 0 ? 1 : -1;   // counter-clockwise (x right, z up): the outward normal of p -> q is (dz, -dx)
  const nrm = (p, q) => { const dx = q.x - p.x, dz = q.z - p.z, l = Math.hypot(dx, dz) || 1; return { x: s * dz / l, z: -s * dx / l }; };
  return pts.map((p, i) => {
    const n1 = nrm(pts[(i - 1 + n) % n], p), n2 = nrm(p, pts[(i + 1) % n]);
    let bx = n1.x + n2.x, bz = n1.z + n2.z;
    const bl = Math.hypot(bx, bz);
    if (bl < 1e-6) { bx = n1.x; bz = n1.z; } else { bx /= bl; bz /= bl; }
    const k = d / Math.max(0.4, bx * n1.x + bz * n1.z);
    return { x: Math.round((p.x + bx * k) * 100) / 100, z: Math.round((p.z + bz * k) * 100) / 100 };
  });
}
// A `poly` area of corners (centre = their mean, radius = the farthest from it) facing f, with the rest from `extra`. Pure.
function polyAreaOf(corners, f, extra) {
  const n = corners.length, cen = { x: corners.reduce((s, p) => s + p.x, 0) / n, z: corners.reduce((s, p) => s + p.z, 0) / n };
  return { shape: 'poly', inner: 0, angle: 360, corners, centre: { x: Math.round(cen.x * 100) / 100, z: Math.round(cen.z * 100) / 100 },
    radius: Math.round(Math.max(...corners.map(p => Math.hypot(p.x - cen.x, p.z - cen.z))) * 100) / 100,
    facing: { x: Math.round(f.x * 1000) / 1000, z: Math.round(f.z * 1000) / 1000 }, ...extra };
}
// Where her dash ends and which way she will face, seen from its start (the Blade's own place) while she is still in it:
// BLADE_SHORT short of the hero (target), at least BLADE_MIN_DASH from the start; facing the hero from there. Pure.
function bladeLanding(start, target) {
  const vx = target.x - start.x, vz = target.z - start.z, l = Math.hypot(vx, vz);
  if (l < 0.05) return { at: { x: start.x, z: start.z }, fwd: { x: 0, z: 1 } };
  const len = Math.max(BLADE_MIN_DASH, l - BLADE_SHORT);
  const at = { x: Math.round((start.x + vx / l * len) * 100) / 100, z: Math.round((start.z + vz / l * len) * 100) / 100 };
  const fx = target.x - at.x, fz = target.z - at.z, fl = Math.hypot(fx, fz);
  return { at, fwd: fl > 0.3 ? { x: fx / fl, z: fz / fl } : { x: vx / l, z: vz / l } };
}
// The Blade's red: its polygon at b.at facing b.fwd, grown BLADE_REACH. b: { at, fwd, left, fill, predicted }. Pure.
function bladeArea(b, points = BLADE_SOLID) {
  if (!b || !b.at || !b.fwd) return null;
  const fl = Math.hypot(b.fwd.x, b.fwd.z);
  if (!(fl > 1e-6)) return null;
  const f = { x: b.fwd.x / fl, z: b.fwd.z / fl };
  return polyAreaOf(growPoly(wavePolyAt(points, b.at, f), BLADE_REACH), f, { fill: Math.round(Math.max(0, Math.min(1, b.fill || 0)) * 100) / 100,
    left: Math.round(Math.max(0, b.left) * 100) / 100, by: 'Dark Moon', type: BLADE_TYPE, blade: true, grown: BLADE_REACH, predicted: !!b.predicted, at: { x: b.at.x, z: b.at.z } });
}
// The mod's Blade (its reader, iteration 51: the prefab's 21-point outline at her place, turned as she faces): the same red as
// read here (bladeArea: filled in, grown) - her place from its first corner and its facing; another outline just grown. Pure.
function bladeGrow(a) {
  if (!a || a.shape !== 'poly' || !Array.isArray(a.corners) || a.corners.length < 3 || a.grown) return a;
  const f = a.facing && Math.hypot(a.facing.x, a.facing.z) > 0.5 ? { x: a.facing.x / Math.hypot(a.facing.x, a.facing.z), z: a.facing.z / Math.hypot(a.facing.x, a.facing.z) } : null;
  if (f && a.corners.length === BLADE_POLY.length) {
    const c0 = a.corners[0], p0 = BLADE_POLY[0];
    const at = { x: c0.x - f.z * p0.x - f.x * p0.y, z: c0.z + f.x * p0.x - f.z * p0.y };
    const g = bladeArea({ at, fwd: f, left: a.left, fill: a.fill });
    if (g) return { ...a, ...g, by: a.by || g.by };
  }
  return { ...a, corners: growPoly(a.corners, BLADE_REACH), radius: Math.round(((a.radius || 0) + BLADE_REACH) * 100) / 100, blade: true, grown: BLADE_REACH };
}
// A read of the Blade (bladeRead) -> { at, fwd, left, fill, predicted, how } or null. r: { created (the Blade's creationTime;
// none: no Blade), start (its place: where her dash began), displacing (her dash still on), pos / fwd (her place and facing),
// chDur / chEl (her channel's duration and time elapsed), prep (atkPrepareDuration), as (her attack speed), seen (game time
// of the first look with the dash over), now (game time) }; me: the hero. Pure.
function bladeNow(r, me) {
  if (!r || typeof r.created !== 'number') return null;
  const total = (typeof r.prep === 'number' && r.prep > 0.05 ? r.prep : BLADE_PREP) / Math.max(0.1, typeof r.as === 'number' && r.as > 0.05 ? r.as : 1);
  if (r.displacing === true) {
    if (!r.start || !me) return null;
    return { ...bladeLanding(r.start, me), left: total + 0.05, fill: 0, predicted: true, how: 'her dash' };
  }
  if (!r.pos || !r.fwd || typeof r.fwd.x !== 'number') return null;
  let left, how;
  if (typeof r.chDur === 'number' && typeof r.chEl === 'number' && r.chDur > 0.05 && r.chDur < 3) { left = r.chDur - r.chEl; how = 'her channel'; }
  else if (typeof r.seen === 'number' && typeof r.now === 'number') { left = total - (r.now - r.seen); how = 'seen'; }
  else { left = total; how = 'assumed'; }
  if (left < -0.1) return null;
  return { at: { x: r.pos.x, z: r.pos.z }, fwd: { x: r.fwd.x, z: r.fwd.z }, left: Math.max(0, left), fill: 1 - Math.max(0, left) / Math.max(0.1, total), predicted: false, how };
}
// One line on a Blade: where she is, how long, where the hero stands. Pure.
function bladeSay(a, me, from, extra = '') {
  const at = a.at || a.centre;
  const dp = areaDepth(me, a, 0);
  const reach = at && Array.isArray(a.corners) ? Math.max(...a.corners.map(p => dist(p, at))) : null;
  return `Dark Moon's Blade (${from}${a.predicted ? ', her dash still on - where it ends' : ''}): her crescent to ${reach != null ? reach.toFixed(1) : '?'} m (grown ${a.grown || 0}), hurting in ${a.left}s - the hero ${at ? dist(me, at).toFixed(1) : '?'}m from her, ${dp > 0 ? `in it (${dp.toFixed(1)}m deep)` : 'clear of it'}${extra}`;
}
// Read by pure getters every look while Dark Moon is in sight and the mod does not list the Blade: the Blade (FindObjectOfType
// - hers; her hallucinations never cast it), her dash, facing and channel, the game's clock.
const BLADE_PATH = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_Ink_BossDarkMoon_Blade)';
const blade = { created: null, seen: null, area: null, n: 0, seenAt: 0, key: null };
async function bladeRead(dm, me) {
  const id = dm.id;
  const [created, start, displacing, fwd, chDur, chEl, now, prep, as] = await Promise.all([peek(BLADE_PATH + '.creationTime'), peek(BLADE_PATH + '.transform.position'),
    peek(`#${id}.Control.isDisplacing`), peek(`#${id}.transform.forward`), peek(`#${id}.Control.ongoingChannels[0].duration`),
    peek(`#${id}.Control.ongoingChannels[0].elapsedTime`), peek('UnityEngine.Time.time'), peek(BLADE_PATH + '.atkPrepareDuration'), peek(`#${id}.Status.attackSpeedMultiplier`)]);
  if (typeof created !== 'number') { blade.created = null; blade.seen = null; blade.area = null; return; }
  if (created !== blade.created) { blade.created = created; blade.seen = null; blade.n++; }
  if (displacing === false && blade.seen == null && typeof now === 'number') blade.seen = now;
  const b = bladeNow({ created, start: vecOf(start), displacing, pos: dm.position, fwd, chDur, chEl, prep, as, seen: blade.seen, now }, me);
  blade.area = b ? bladeArea(b) : null;
  if (blade.area) blade.area.how = b.how;
}
async function roomCentre() {
  if (bh.centre !== undefined) return bh.centre;
  const vec = v => v && typeof v.x === 'number' && typeof v.z === 'number' ? { x: v.x, z: v.z } : null;
  bh.centre = vec(await peek('Sky_BossRoomCenter.instance.transform.position')) || (bossEntry && bossEntry.arena) || vec(await peek('Room_BossArena.instance.center'));
  return bh.centre;
}
// White Night's Cataclysm (see safeSpot): pure reads, polled every 250 ms while she is in sight or
// the hero is NotSafe. The first time each is found its numbers are logged once (prefab values).
//   on    the Cataclysm is going (its ability exists)
//   safe  { points, radius, left, how } - this wave's circles, or null between waves / none
const CATA_PATH = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_Ink_BossWhiteNight_Cataclysm)';
const SZ_PATH = 'UnityEngine.Object.FindObjectOfType(Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone)';
const cata = { readAt: 0, on: false, safe: null, cfg: null, szCfg: null, key: null, spot: null, walkAt: 0, said: null, blind: 0, waves: 0 };
const vecOf = v => v && typeof v.x === 'number' && typeof v.z === 'number' ? { x: v.x, z: v.z } : null;
async function cataclysmRead(areas) {
  const listed = (areas || []).filter(a => a && a.shape === 'safe' && a.centre);
  const [waves, pts, r, end, now] = await Promise.all([peek(CATA_PATH + '.cWaveCount'),
    listed.length ? null : peek(SZ_PATH + '._points'), listed.length ? null : peek(SZ_PATH + '._radius'),
    listed.length ? null : peek(SZ_PATH + '._endTime'), listed.length ? null : peek('UnityEngine.Time.time')]);
  cata.on = typeof waves === 'number';
  if (cata.on && !cata.cfg) {
    const names = ['cWaveCount', 'cCastDelay', 'cInterval', 'cPostDelay', 'heroSafeZoneDeviation', 'safeZoneRadius', 'safeZoneReduceRatio', 'safeZoneBaseCount', 'globalSafeZoneRange', 'startDelay', 'startTeleportDuration', 'startDaze', '_isRage'];
    const vals = await Promise.all(names.map(n => peek(CATA_PATH + '.' + n)));
    cata.cfg = Object.fromEntries(names.map((n, i) => [n, vals[i]]));
    log(`  cataclysm: its numbers ${JSON.stringify(cata.cfg)}`);
  }
  if (listed.length) { cata.safe = { points: listed.map(a => vecOf(a.centre)), radius: listed[0].radius, left: listed[0].left, how: '/threats' }; return; }
  const points = Array.isArray(pts) ? pts.map(vecOf).filter(Boolean) : [];
  const left = typeof end === 'number' && typeof now === 'number' ? end - now : null;
  cata.safe = points.length && typeof r === 'number' && r > 0 && (left === null || left > -0.3) ? { points, radius: r, left: left === null ? 2 : left, how: 'read' } : null;
  if (cata.safe && !cata.szCfg) {
    const names = ['checkInterval', 'endDelay'];
    const vals = await Promise.all(names.map(n => peek(SZ_PATH + '.' + n)));
    cata.szCfg = Object.fromEntries(names.map((n, i) => [n, vals[i]]));
    log(`  cataclysm: the safe zone's numbers ${JSON.stringify(cata.szCfg)}`);
  }
}
// The Ink boss room's damaging ground, read (Ink_BossRoomDamageGround: position, radius, whether it is
// on). Found once (/reflect/find, again every 5 s while none), its state read every 1.5 s.
const grounds = { findAt: 0, refs: [], readAt: 0, list: [], said: false };
async function readGrounds() {
  const now = Date.now();
  if (!grounds.refs.length && now - grounds.findAt > 5000) {
    grounds.findAt = now;
    try { grounds.refs = ((await get('/reflect/find', { type: 'Ink_BossRoomDamageGround', limit: 10 })).objects || []).map(o => o.$ref).filter(x => x != null); } catch { grounds.refs = []; }
  }
  if (!grounds.refs.length || now - grounds.readAt < 1500) return;
  grounds.readAt = now;
  const rows = await Promise.all(grounds.refs.map(async ref => {
    const [pos, radius, on, ratio, interval] = await Promise.all(['position', 'radius', '_spawnGroundEnable', 'dmgMaxHealthRatio', 'interval'].map(n => peek(`$${ref}.${n}`)));
    return { centre: vecOf(pos), radius, on, ratio, interval };
  }));
  if (!grounds.said && rows.some(g => g.centre)) { grounds.said = true; log(`  damaging ground: ${rows.filter(g => g.centre).map(g => `(${g.centre.x.toFixed(1)}, ${g.centre.z.toFixed(1)}) r ${g.radius} ${g.on ? 'on' : 'off'}, ${g.ratio} of max hp every ${g.interval}s`).join('; ')}`); }
  grounds.list = rows.filter(g => g.centre && typeof g.radius === 'number' && g.on === true);
}
// One tick of the Cataclysm: into this wave's circle (safeSpot / safeMove / safeDashCell). Returns
// 'inside' (stand, shoot), 'moving', or null (nothing known). A new wave (other points) picks anew.
async function cataclysmStep(me, grid, dashReady, poolList, traced) {
  const s = cata.safe;
  const key = s.points.map(p => p.x.toFixed(1) + ',' + p.z.toFixed(1)).join(';');
  if (key !== cata.key) {
    cata.key = key; cata.walkAt = 0; cata.waves++;
    const rs = await Promise.all(s.points.map(p => get('/nav/path', { x: p.x, z: p.z }).catch(() => null)));
    const lens = rs.map((r, i) => r && r.status && r.status !== 'none' && r.destination && dist(r.destination, s.points[i]) < s.radius && typeof r.length === 'number' ? r.length : null);
    cata.spot = safeSpot(s, me, poolList, lens);
    if (cata.spot) log(`  cataclysm: wave ${cata.waves} - ${s.points.length} safe circles of ${s.radius.toFixed(1)}m (${s.how}), ${s.left.toFixed(1)}s left; going to (${cata.spot.p.x.toFixed(1)}, ${cata.spot.p.z.toFixed(1)}), ${cata.spot.straight.toFixed(1)}m${cata.spot.len != null ? ` (${cata.spot.len.toFixed(1)}m walk)` : ''}${cata.spot.onGround ? ', on the damaging ground (nothing better)' : ''} | the others ${s.points.filter((_, i) => i !== cata.spot.i).map(p => dist(me, p).toFixed(1) + 'm').join(', ')}`);
  }
  const sp = cata.spot;
  if (!sp) return null;
  const m = safeMove(sp, s.radius, me, s.left, dashReady);
  if (traced) trace(`  cataclysm ${m.act} d ${m.d.toFixed(1)} need ${m.need.toFixed(1)} left ${s.left.toFixed(2)}`);
  if (m.act === 'stay') return 'inside';
  if (m.act === 'dash') {
    const cell = safeDashCell(grid, me, sp.p, s.radius);
    if (cell) {
      log(`  dash: cataclysm - ${m.need.toFixed(1)}m from the safe circle, ${s.left.toFixed(1)}s left -> ${cell.out.toFixed(1)}m from it`);
      await tryPost('/hero/cast', { slot: 'Movement', x: cell.p.x, z: cell.p.z, move: false });
      cata.walkAt = 0;
      return 'moving';
    }
  }
  if (Date.now() - cata.walkAt > 500) { cata.walkAt = Date.now(); await tryPost('/hero/move', { x: sp.p.x, z: sp.p.z }); }
  return 'moving';
}

async function readHits() {
  try {
    const dmg = await get('/damage', { since: damageSeq, limit: 200 });
    const hits = damageSeq >= 0 ? dmg.hits.filter(Boolean) : [];
    for (const h of hits) hitTotal += h.amount || 0;
    for (const h of hits) { const k = (h.by || '?') + (h.caster && h.caster !== h.by ? '/' + h.caster : '') + (h.overTime ? ' (dot)' : ''); tookAcc[k] = (tookAcc[k] || 0) + h.amount; }
    notePools(pools, { hits, now: Date.now() });
    for (const h of hits) blind.queue.push({ t: Date.now(), h });
    for (const h of hits) if (BURN_SHOT.test(h.by || '') && !h.overTime) log(fireShotSay(fireSeen, h, Date.now()));   // iteration 51
    for (const h of hits) if (!h.overTime && watchOf(h.by)) log(watchHitSay(watchSeen, h, Date.now(), { at: fireSeen.dashAt, why: fireSeen.dashWhy }, watchReaders));   // iteration 54
    for (const h of hits) if (/^LavaLand_Lava/.test(h.by || '')) { lavaHit.at = Date.now(); lavaHit.total += h.amount || 0; lavaHit.recent.push({ t: lavaHit.at, amount: h.amount || 0 }); }
    while (lavaHit.recent.length && Date.now() - lavaHit.recent[0].t > 2000) lavaHit.recent.shift();
    if (blind.queue.length > 50) blind.queue.splice(0, blind.queue.length - 50);
    for (const h of hits) if (h && h.from && BH_HIT.test(h.by || '')) {
      const now = Date.now();
      if (!bhHit.last || now - bhHit.last > 4000) bhHit.first = now;
      bhHit.last = now; bhHit.at = { x: h.from.x, z: h.from.z };
    }
    damageSeq = dmg.last;
    return hits;
  } catch { return []; }
}
const tookLine = () => { const s = Object.entries(tookAcc).sort((x, y) => y[1] - x[1]).map(([k, v]) => Math.round(v) + ' from ' + k).join(', '); tookAcc = {}; return s; };
const burning = hero => (hero && hero.statusEffects || []).some(e => e.type === 'Se_Elm_Fire');

// Before walking somewhere out of a fight (a loot target, the exit): while a pool burns on the way
// or the hero is still on fire, stand clear and let it go out, up to `maxMs` - and FIRE_WAIT_ROOM in
// all per room. Standing in a pool: step out of it, away from its centre. Returns early when enemies
// come (lootAbort) or lava hits.
const FIRE_WAIT_ROOM = 15000, QUIET_POOL_WAIT = 4000;
let fireWaited = 0;   // ms waited in this room (auto() resets it with the pools)
const groundHeld = { said: false, stopped: false };   // iteration 43: fight()'s hold off the Ink ground (groundHold), per room
async function waitOutFire(to, what, maxMs = 8000) {
  const t = Date.now();
  maxMs = Math.min(maxMs, FIRE_WAIT_ROOM - fireWaited);
  if (maxMs <= 0) return;
  let said = false, h0 = null, h = null;
  while (Date.now() - t < maxMs) {
    if (await enemyNear()) break;
    // Lava is not waited on: its ticks grow while the hero stands in it (LavaLand_Lava).
    if ((await readHits()).some(x => /^LavaLand_Lava/.test(x.by || ''))) break;
    const th = await get('/threats', { radius: 25 }).catch(() => null);   // the mod's zones, if listed
    if (th) notePools(pools, { areas: th.areas || [], now: Date.now() });
    h = await get('/hero').catch(() => null);
    if (!h) break;
    if (!h0) h0 = h;
    // Iteration 43: the Ink ground no longer listed - destroyed (groundGone).
    if (th && groundGone(pools, th.areas, h.position, Date.now())) log(`  damaging ground: no longer listed - gone`);
    const now = Date.now();
    const p = poolInWay(pools, now, h.position, to);
    // Iteration 28: timed red on the first WALKUP_LOOK m of the way or under the hero (redInWay: a strike, a filling telegraph,
    // a lobbed shot's landing) that lands within RED_WAIT_LEFT s - waited out (stepped out of when under the hero), for
    // RED_WAIT_MAX ms at most.
    const rw = !p && now - t < RED_WAIT_MAX ? redInWay(walkReds(th), h.position, alongTo(h.position, to, WALKUP_LOOK), RED_WAIT_LEFT) : null;
    if (rw) {
      if (!said) { said = true; log(`  red: a ${rw.area.shape} of ${rw.area.by || rw.area.type || '?'} (${rw.area.radius} m, lands in ${rw.area.left}s) ${rw.inside ? 'under the hero - stepping out' : 'on the way'} - waiting before ${what}`); }
      if (rw.inside) { const o = outOf(rw.area, h.position); await tryPost('/hero/move', { x: o.x, z: o.z }); } else await tryPost('/hero/move_dir', { x: 0, z: 0 });
      await sleep(200);
      continue;
    }
    const onFire = burning(h);
    // Iteration 17: the burn alone is no reason to stand - it lapses as fast on the move (a status on the hero,
    // StackedStatusEffect decay) and only a fire hit relights it; the pools on the way are what is waited for.
    // run-023 stood 1.8 + 1.8 + 0.8 s "burning - waiting before ..." in three LavaLand rooms, and 2.6 s at
    // Combat_4_2 where the pool it waited for had 1.5 s left (the rest was the burn).
    if (!p) break;
    // Losing health while waiting (a fire source not known, still lighting the burn): standing is
    // no cure - go on (run-012: 199 -> 0 in the 8.2 s of standing at a campfire). Death: enemyNear.
    if (h0.hp - h.hp > 0.12 * h.maxHp) { log(`  fire: ${Math.round(h0.hp - h.hp)} hp lost standing - not waiting any longer`); break; }
    if (!said) { said = true; log(`  fire: ${p ? `${p.fixed ? `a fixed fire (${p.by})` : 'a pool'} (${p.radius.toFixed(1)}m, ${((p.until - now) / 1000).toFixed(1)}s more)${dist(h.position, p.centre) < p.radius + 0.8 ? ' under the hero' : ' on the way'}` : 'burning'} - waiting before ${what}`); }
    if (p && dist(h.position, p.centre) < p.radius + 0.8) {
      // Iteration 21: in LavaLand to a dry cell off every pool (poolExitCell), not straight away from its centre (run-031:
      // onto the lava twice); none, or the pool about to go out: stand.
      if (onLavaZone()) {
        const g = p.fixed || p.until - now > 600 ? await get('/nav/grid', { radius: 8, step: 1 }).catch(() => null) : null;
        const c = g ? poolExitCell(g, h.position, pools.filter(q => q.born <= now && now < q.until)) : null;
        if (c) await tryPost('/hero/move', { x: c.p.x, z: c.p.z });
        else { await tryPost('/hero/stop'); await tryPost('/hero/move_dir', { x: 0, z: 0 }); }
      } else {
        const dd = Math.max(0.1, dist(h.position, p.centre)), k = (p.radius + 1.5) / dd;
        await tryPost('/hero/move', { x: p.centre.x + (h.position.x - p.centre.x) * k, z: p.centre.z + (h.position.z - p.centre.z) * k });
      }
    } else await tryPost('/hero/move_dir', { x: 0, z: 0 });
    await sleep(300);
  }
  fireWaited += Date.now() - t;
  if (said && h0 && h) log(`  fire: waited ${((Date.now() - t) / 1000).toFixed(1)}s, hp ${Math.round(h0.hp)} -> ${Math.round(h.hp)}${burning(h) ? ', still burning' : ''}${Object.keys(tookAcc).length ? ' | took ' + tookLine() : ''}`);
}

// Where a boss room's fight starts, when no boss is in sight. run-007's Sky boss room (Nyx): nobody
// within 300 m for 40 s, no combat areas listed, the hero sent to the shut exit - "quiet but exit
// closed" - and the bot took that for the boss down (a false zone-2 clear). Decompiled: a boss room
// starts with DewBossRoomEntry, a DewAllHeroesPresentZone that plays the intro and teleports the
// heroes into the arena once they are all inside it; Room_BossArena is the arena (centre, radius).
// Both read by pure getters, once per room (bossEntry, reset by auto()); either may be missing.
// run-008 (the monitor, by hand): Room_Sky_Boss_0 is two navmesh islands - the start plateau (hero,
// a Shrine of Guidance) and the arena (the combat area, the exit, Room_BossArena) - joined only by
// the entry: a GameObject "Boss Room Entry" at (5.5, 82.8) at the plateau's north end, with a
// DewHeroesTeleporter (decompiled: DewBossRoomEntry requires one on the same object, and its zone
// teleports everyone to its `destination`, (39.1, -40.3), when all heroes stand in it). It is not in
// /interactables nor /state.room. Walking into it: teleported ~3 m short of its centre, an ~8 s
// cutscene, then Nyx. /hero/move to the arena's combat area answered with the hero's own spot (no
// path) and the hero stood 40 s - the iter-7 code never got to the entry because an inactive combat
// area came first in the goal list. The entry is read three ways, in case one path does not resolve.
// With the iter-8 mod proposal applied, /state.room.bossEntry says it directly.
async function bossRoomGoals(room = {}) {
  const out = { entry: null, arena: null, done: false, since: 0, last: null, how: null };
  const vec = v => v && typeof v.x === 'number' && typeof v.z === 'number' ? { x: v.x, z: v.z } : null;
  const read = async path => { try { return vec(await get('/reflect/get', { path })); } catch { return null; } };
  if (room.bossEntry) { out.entry = vec(room.bossEntry.position); out.how = '/state bossEntry'; if (room.bossEntry.waiting === false) out.done = true; }
  // Whether its zone is still waiting for the heroes: it disables itself once it has fired (run-009's
  // Forest boss room: no boss in sight for the intro's 11 s - an entry already used is not walked to).
  const waiting = async base => { try { return await get('/reflect/get', { path: base + '.GetComponent(DewAllHeroesPresentZone).enabled' }); } catch { return null; } };
  let base = null;
  // /state says there is none (the mod lists `bossEntry: null`): the reads below would each fail and
  // be retried (get(): 3 x 300 ms) - run-015's Forest and DarkCave boss rooms spent ~2 s on them.
  const none = 'bossEntry' in room && room.bossEntry === null;
  if (!out.entry && !none) { base = 'UnityEngine.Object.FindObjectOfType(DewBossRoomEntry)'; out.entry = await read(base + '.transform.position'); if (out.entry) out.how = 'DewBossRoomEntry'; }
  for (const type of ['DewBossRoomEntry', 'DewHeroesTeleporter']) {
    if (out.entry || none) break;
    try {
      const f = await get('/reflect/find', { type, limit: 5 });
      const o = (f.objects || []).find(x => /entry/i.test(x.name || '')) || (f.objects || [])[0];
      if (o && o.$ref != null) { base = '$' + o.$ref; out.entry = await read(base + '.transform.position'); if (out.entry) out.how = `${type} "${o.name}"`; }
    } catch { }
  }
  if (out.entry && base && (await waiting(base)) === false) { out.done = true; out.how += ', already used'; }
  out.arena = vec(await peek('Room_BossArena.instance.center'));
  return out;
}
let bossEntry = null;   // bossRoomGoals() for this room, read the first time no boss is in sight

// The boss-room entry to walk into, or null once it is behind us: the hero was teleported (a jump
// of > 12 m between two looks), stood at it 6 s with nothing happening, or it cannot be walked to.
async function bossEntryGoal(me, room) {
  if (!bossEntry) {
    bossEntry = await bossRoomGoals(room);
    log(`  boss room, no boss in sight - ${bossEntry.entry ? `${bossEntry.done ? 'not walking to' : 'walking into'} the boss-room entry (${bossEntry.entry.x.toFixed(1)}, ${bossEntry.entry.z.toFixed(1)}; ${bossEntry.how}, ${dist(me, bossEntry.entry).toFixed(1)}m)` : 'no boss-room entry found'}${bossEntry.arena ? `, the arena at (${bossEntry.arena.x.toFixed(1)}, ${bossEntry.arena.z.toFixed(1)})` : ''}`);
  }
  const e = bossEntry.entry;
  if (!e || bossEntry.done) return null;
  const endEntry = why => { bossEntry.done = true; log(`  boss-room entry: ${why} - hero at (${me.x.toFixed(1)}, ${me.z.toFixed(1)})`); return null; };
  if (bossEntry.last && dist(me, bossEntry.last) > 12) return endEntry('teleported');
  bossEntry.last = me;
  // run-008: its centre lies ~3 m past the plateau's walkable edge, and the teleport came 3.3 m short
  // of it. The zone looks for the heroes once a second (DewAllHeroesPresentZone).
  if (dist(me, e) < 4.5) { if (!bossEntry.since) bossEntry.since = Date.now(); else if (Date.now() - bossEntry.since > 6000) return endEntry('stood at it 6 s, nothing happened'); }
  return e;
}

const NOT_AT_ENEMIES = new Set();
// Casts that do not hold the fight's loop (iteration 18). /hero/cast (mods/DevTools/Api/ActionApi.cs Cast)
// casts at once, then waits up to 0.35 s for the skill to start charging ("sampling") and answers only
// after that - for a skill that does not charge, the full 0.35 s. Every cast in run-024/025's boss traces
// held the loop 0.39-0.68 s (0.42 on average, against 0.08 for a look with no cast), ~0.5 casts a second:
// a fifth of a fight with no attack order (attack_in_place is one shot a call), no look at the red and no
// move. run-025's PillarOfStars (200) showed up during such a wait (drawn at ~35.37, first looked at 35.52
// with 0.45 s left). Refusals (cooldown, range, not a valid target) are thrown before the wait, so they
// come back within a frame or two. So: the reply is waited for CAST_REPLY_MS; if it has not come, the cast
// is under way - the loop goes on (looks, walks, dashes, attacks) and the next skill waits for this one's
// reply (castBusy). A skill seen to charge (the reply's `charged`), Precision Shot, the self-cleanse and the
// charged shot's number read are cast as before.
// Iteration 19: the mod's `sample` (proposals/iter-18-mod.md 2, applied for run-028) - how long /hero/cast looks
// for a charging skill before it answers. A skill type whose reply said charged: false after the full look
// (NO_CHARGE) is cast with sample SHORT_SAMPLE: the answer comes in ~0.05 s and a frame or two, inside the wait
// (SHORT_REPLY_MS), so the loop has the real reply (no cast left in the server's hands, no two casts' coroutines
// on the virtual mouse at once). The first cast of each type looks the full 0.35 s, as before, to learn it.
const QUICK_CASTS = true, CAST_REPLY_MS = 120, SHORT_SAMPLE = 0.05, SHORT_REPLY_MS = 250;
const CHARGING = new Set();   // skill types whose cast replied charged: true
const NO_CHARGE = new Set();  // skill types whose cast replied charged: false after the full look (iteration 19)
function noteCharge(rr, type, sampled) {
  if (!rr || rr.error) return;
  if (rr.charged) CHARGING.add(type);
  else if (rr.charged === false && !sampled) NO_CHARGE.add(type);
}
async function castQuick(body, type, nk, busy) {
  const short = NO_CHARGE.has(type) && !CHARGING.has(type);
  if (short) body = { ...body, sample: SHORT_SAMPLE };
  const p = tryPost('/hero/cast', body);
  const r = await Promise.race([p, sleep(short ? SHORT_REPLY_MS : CAST_REPLY_MS).then(() => null)]);
  if (r) { noteCharge(r, type, short); return r; }
  busy.p = p; busy.since = Date.now();
  p.then(rr => {
    if (busy.p === p) busy.p = null;
    noteCharge(rr, type, short);
    if (rr && rr.error && /not a valid target/.test(rr.error)) NOT_AT_ENEMIES.add(nk);
  });
  return { quick: true };
}
// Skills that hold the hero (lockOf above): read once per type, by any instance's id (a worn skill, the memory in
// hand). skillLocks: type -> { channel, blocks, post, lock }; heavy(): known to hold the hero HEAVY_LOCK or more.
const skillLocks = new Map(), lockPending = new Set();
const skillSaid = new Set();   // iteration 20: skills whose range and cooldown were logged
const heavy = type => KNOWN_HEAVY.has(type) || ((skillLocks.get(type) || {}).lock || 0) >= HEAVY_LOCK;
async function learnLock(id, type) {
  if (!id || !type || skillLocks.has(type) || lockPending.has(type)) return skillLocks.get(type) || null;
  lockPending.add(type);
  try {
    const base = '#' + id + '.currentConfig.';
    const [channel, blocks, post] = await Promise.all(['channel.duration', 'channel.blockedActions', 'postDelay'].map(n => peek(base + n)));
    if (channel === undefined && post === undefined) {   // not read (gone, or no such path): not asked again
      skillLocks.set(type, { lock: 0, failed: true });
      log(`  skill ${type}: its channel and post-delay could not be read${KNOWN_HEAVY.has(type) ? ' - heavy all the same (KNOWN_HEAVY)' : ''}`);
      return null;
    }
    const l = { channel, blocks, post, lock: lockOf({ channel, blocks, post }) };
    skillLocks.set(type, l);
    log(`  skill ${type}: a cast holds the hero ${l.lock}s (channel ${channel}s blocking ${JSON.stringify(blocks)}, post-delay ${post}s)${heavy(type) ? ' - heavy: cast only with nothing near, not taken in place of another' : ''}`);
    return l;
  } finally { lockPending.delete(type); }
}
// Lacerta's R, Precision Shot (history/it9/skills/Ai_R_PrecisionShot*.cs): a charged shot - damage
// lerps damageMin -> damageMax and the reach lengthMin -> lengthMax by the charge (0..1, full after
// channel.chargeFullDuration), a crit above critThreshold (0.5), full above fullGraceThreshold. The bot
// releases it after 0.3 s everywhere, and in run-010's three boss fights cast it 6 times. Whether a
// longer charge at a boss pays needs its numbers (prefab values the code does not show): read once
// in a boss fight, while it charges (the ability exists only then). Pure reads.
const precision = { cfg: null, tries: 0 };
async function precisionNumbers() {
  precision.tries++;
  const base = 'UnityEngine.Object.FindObjectOfType(Ai_R_PrecisionShot)';
  const names = ['channel.chargeFullDuration', 'fullGraceThreshold', 'lengthMin', 'lengthMax', 'Network_chargeAmount', 'cooldownRefundRatioOnCancel', 'doUnstoppable',
    'channel.canMove', 'channel.selfSlowAmount', 'channel.castDazeDuration', 'channel.completeDuration'];
  const vals = await Promise.all(names.map(n => peek(base + '.' + n)));
  if (vals.some(v => v !== undefined && v !== null)) precision.cfg = Object.fromEntries(names.map((n, i) => [n, vals[i]]));
}
// Iteration 20: Precision Shot where it reaches. Every R cast at a boss in runs 018-029 (~60) went off at 6.0-6.5 m: the
// rules were "d <= the skill's range (5, its lengthMin) + 1.5" and "not within 6 m of a boss" - a 0.5 m window the hero
// stood in 3-9% of the boss traces' looks (plan() keeps 7.35 m at a boss, 6.5 m in rooms). So the skill all the dream
// dust goes into (+4 by zone 1, +8..10 by zone 2) went off about once a boss fight (run-029: Belphomet 1, the Seeker 2,
// Nyx 1 in 47 s), though it comes back within ~2 s (run-028: two casts 1.9 s apart) and hits for ~3-4 basic attacks
// (run-029: Nyx -1529 within 0.4 s of one). Its reach grows with the charge (lengthMin -> lengthMax, 5 -> 16 m, linear
// over chargeFullDuration 1 s - ChargingChannel.HandleOnTick): the charge is now what the distance needs (+P_MARGIN, a
// little for the frame the release takes), at least the old 0.3 s, at most P_CAP (P_CAP_BOSS at a boss: the loop waits
// out the charge). Pure (tests/iter20.test.mjs).
const P_MARGIN = 0.4, P_MIN = 0.3, P_CAP = 0.55, P_CAP_BOSS = 0.45, P_LAG = 0.04, P_BOSS_MIN_D = 5;
function precisionCharge(d, cfg, cap) {
  const num = (k, v) => cfg && typeof cfg[k] === 'number' && cfg[k] > 0 ? cfg[k] : v;
  const lo = num('lengthMin', 5), hi = num('lengthMax', 16), full = num('channel.chargeFullDuration', 1);
  if (hi <= lo) return d + P_MARGIN <= lo ? P_MIN : null;
  const c = Math.max(P_MIN, Math.max(0, (d + P_MARGIN - lo) / (hi - lo)) * full + P_LAG);
  return c <= cap ? Math.round(c * 100) / 100 : null;
}
// Iteration 21: the far shot. run-030's Infernus shield (29.4 s, the second Flame of Pyrana at (-13.4, 41.6) - run-023's
// pillar 2 stood on the same spot - 9.2 m of lava away, "no dry place within 7.9 m of it"): the hero came to 10.9 m of it
// on dry ground and never shot it; the shield ran out on its own. Precision Shot pierces (dmgAmpPerHit) and its reach is
// 16 m at a full charge. Charged up to P_CAP_FAR (short of chargeFullDuration: a hold past completeDuration cancels it)
// at a pillar that must die, or at a LavaLand target with lava on the way; precisionReach is how far that goes (the
// shooting place is looked for P_FAR_KEEP inside it). Pure (tests/iter21.test.mjs).
const P_CAP_FAR = 0.95, P_FAR_KEEP = 0.5, P_FAR_CLEAR = 6, P_FULL = true;
function precisionReach(cfg, cap) {
  const num = (k, v) => cfg && typeof cfg[k] === 'number' && cfg[k] > 0 ? cfg[k] : v;
  const lo = num('lengthMin', 5), hi = num('lengthMax', 16), full = num('channel.chargeFullDuration', 1);
  if (hi <= lo) return lo - P_MARGIN;
  return lo + Math.max(0, cap - P_LAG) / full * (hi - lo) - P_MARGIN;
}
const precisionWorn = hero => ((hero && hero.skills) || []).some(k => /PrecisionShot/.test(k.type || '') && ['Q', 'W', 'E', 'R'].includes(k.slot));
// Its damage numbers (the projectile's damageMin/damageMax, ScalingValue.valueString), read once while one flies - for
// deciding on a full charge at bosses later. Pure reads, not waited for.
async function precisionDamage() {
  if (precision.dmg || (precision.dmgTries = (precision.dmgTries || 0) + 1) > 4) return;
  const base = 'UnityEngine.Object.FindObjectOfType(Ai_R_PrecisionShot_Projectile)';
  const names = ['damageMin.valueString', 'damageMax.valueString', 'critThreshold', 'dmgAmpPerHit', 'stunDurationMin', 'stunDurationMax', 'chargeAmount', 'endDistance', 'procCoefficient', 'collisionRadius'];
  for (let k = 0; k < 3 && !precision.dmg; k++) {
    const vals = await Promise.all(names.map(n => peek(base + '.' + n)));
    if (vals[0] != null || vals[1] != null) {
      precision.dmg = Object.fromEntries(names.map((n, i) => [n, vals[i]]));
      log(`  precision shot: its damage ${JSON.stringify(precision.dmg)}`);
    } else await sleep(40);
  }
}

// ----- iteration 32: overkill-aware casts --------------------------------------------------------------------------------
// The user: "the bot does not account at all for how much damage a monster needs - it uses its most powerful skill on a
// small spider, then fights stronger enemies with basic attacks". And: "overkill IS justified when the skill kills a crowd".
// Every skill went at the fight's one target (the nearest, or the miniboss) the moment it was ready. Runs 030-041 (94
// combat rooms): Precision Shot went off once every 8.8-10.3 s of fighting - on cooldown - and the target at the status
// looks had under 30% of its damage left in 80% (zone 0), 50% (zone 1) and 68% (zone 2) of them: ~70-80% of its damage
// wasted. Its damage per cast from the boss traces (the boss's hp drop, the basic attacks' pace taken off): 0.3-0.45 s
// charge ~580 (zone 0) / ~810 (zone 1) / ~1200 (zone 2), full charge ~910 / ~1640 / ~2110 (so no crit bonus to speak of:
// the full/short ratio 1.6-2.0 is the 1.5ap -> 8ap lerp alone). The enemies: Scarab 46, Little Baam 105, Cave Spider 299,
// Leaf Hound 491 against Spider Warrior 1647, Hellfire Spider 1923, Dark Elemental 2133, Soul Spearman 2687. In 5 of 12
// miniboss fights in rooms the shot never went at the miniboss (spent on the small ones before it came: run-040's
// Forest_Combat_0 - two full-charge shots into 34-60 hp Scarabs and spiders, then the 1647 hp Spider Warrior with it down).
//
// So a skill's cast is weighed by the damage that lands: each enemy it would hit counts min(its hp + shield, the skill's
// damage), plus OK_KILL of the damage for each it kills (a crowd killed is worth the overkill). A big skill (a cooldown of
// OK_BIG_CD s or more between charges, its damage known) goes off when that is at least OK_WORTH of its damage; below that
// it is held while an enemy worth it on its own is in sight (for up to OK_HOLD_MAX s), else it goes at the best there is if
// that is OK_LAST of its damage, else the cooldown waits. Cheap skills go on cooldown as before. Where a skill has an area,
// the aim is the one that lands the most: a line through the most (Precision Shot pierces), a cone's direction, a circle's
// centre (enemies led by their pace), a splash round the right one. Zone-boss fights and a shielded boss's pillars keep
// the aim at the target (iteration 26's holds come first, untouched). Pure (tests/iter32.test.mjs).
const OK_WORTH = 0.5, OK_KILL = 0.15, OK_LAST = 0.35, OK_BIG_CD = 6, OK_HOLD_MAX = 12, OK_ENT_R = 0.5, OK_LEAD = 0.3, OK_LEAD_MAX = 2;
const P_DMG_MIN = '1.5ap', P_DMG_MAX = '8ap', P_CRIT = 0.75, P_CRIT_MULT = 1, P_WIDTH = 1, P_FULL_GAIN = 1.25;
const effHp = e => Math.max(0, ((e && e.hp) || 0) + ((e && e.shield) || 0));
// The skills' areas. The radii live in the prefabs (DewCollider), not in the code; the code (history/it9/skills) shows the
// kind: Precision Shot a piercing projectile (dmgAmpPerHit), Dark Bolt pierces once (penetrationCount 1), Scattershot a fan
// of +-30 deg (maxAngle), Ignite a DewCollider round its target, Chain Lightning 6 jumps (maxChainCount), Smite/Purgatory a
// circle at the point after a delay (0.5 s), Starfall round the hero, Small Molten Core a dragon (summon). 'self': a buff,
// a shield, a move - no aim to choose. What the table does not say comes from the skill's own castMethod (the indicator the
// player sees: _radius, _angle, _length, _width - read once per skill, learnShape) or its aim type.
const SKILL_AREA = {
  St_R_PrecisionShot: { kind: 'line', pierce: true, width: P_WIDTH },
  St_C_DarkBolt: { kind: 'line', pierce: true, maxHits: 2, width: 0.8 },
  St_C_DarkSpear: { kind: 'line', pierce: true, width: 1.2 },
  St_C_PressurePoint: { kind: 'line', pierce: true, width: 1.5 },
  St_C_Pew: { kind: 'single' },
  St_C_BeamOfLight: { kind: 'single' },
  St_R_Scattershot: { kind: 'cone', angle: 60 },
  St_R_RepulsiveShield: { kind: 'cone' },
  St_R_GlacialHammer: { kind: 'cone' },
  St_R_GreatFrostSword: { kind: 'cone' },
  St_C_MagicSword: { kind: 'cone' },
  St_R_Smite: { kind: 'circle', r: 2.5, lead: 0.5 },
  St_C_Purgatory: { kind: 'circle', r: 3, lead: 0.5 },
  St_E_WinterDive: { kind: 'circle', r: 3, moves: true },
  St_R_Ignite: { kind: 'splash', r: 3 },
  St_E_ChainLightning: { kind: 'chain', hops: 6, hop: 4 },
  St_C_Starfall: { kind: 'around' },
  St_Q_IncendiaryRounds: { kind: 'self' }, St_C_MassProtection: { kind: 'self' }, St_R_Somersault: { kind: 'self' },
  St_E_Rewind: { kind: 'self' }, St_E_UmbralEdge: { kind: 'self' }, St_C_BackStep: { kind: 'self' }, St_C_IceBlock: { kind: 'self' },
  St_L_SmallMoltenCore: { kind: 'self' }, St_E_MassCleanse: { kind: 'self' },
};
// A skill's area: the table, else its castMethod (cm: { radius, angle, length, width, range } as read), else its aim type.
// onlyTarget: a skill that carries the hero onto its target (lunges, charges, a dive) keeps its target as before.
function skillShape(k, cm, onlyTarget = false) {
  const tr = (k && k.trigger) || {}, type = (k && k.type) || '', t = SKILL_AREA[type] || null, c = cm || {};
  const num = v => typeof v === 'number' && v > 0 ? v : null;
  const range = num(tr.range) || num(c.range) || 0;
  let kind = t ? t.kind : tr.aim === 'Point' ? 'circle' : tr.aim === 'Cone' ? 'cone' : tr.aim === 'Arrow' || tr.aim === 'Target' ? 'single' :
    tr.aim === 'None' ? (num(c.radius) ? 'around' : 'self') : 'unknown';
  if ((onlyTarget || (t && t.moves)) && kind !== 'self') kind = 'single';
  const r = (t && t.r) || num(c.radius) || (kind === 'circle' ? 2.5 : kind === 'around' ? (range || 4) : 0);
  return {
    type, kind, range, r, angle: (t && t.angle) || num(c.angle) || 60, width: (t && t.width) || num(c.width) || 1,
    len: num(c.length) || range, pierce: !!(t && t.pierce), maxHits: (t && t.maxHits) || 0, hops: (t && t.hops) || 0, hop: (t && t.hop) || 0,
    lead: (t && t.lead) || 0, onlyTarget: !!(onlyTarget || (t && t.moves)), src: t ? 'table' : cm ? 'castMethod' : 'aim',
  };
}
// Where an enemy will be `lead` s on, by the pace seen (e.vel, m/s), at most OK_LEAD_MAX m on.
function ahead(e, lead) {
  const v = e.vel, p = e.position;
  if (!v || !lead) return p;
  let dx = v.x * lead, dz = v.z * lead;
  const l = Math.hypot(dx, dz);
  if (l > OK_LEAD_MAX) { dx *= OK_LEAD_MAX / l; dz *= OK_LEAD_MAX / l; }
  return { x: p.x + dx, z: p.z + dz };
}
// Its pace from the last look (prev: id -> { x, z, t }); pure but for the map it keeps.
function notePace(prev, ents, now) {
  for (const e of ents) {
    const q = prev.get(e.id);
    if (q && now - q.t > 50 && now - q.t < 1500) e.vel = { x: (e.position.x - q.x) / ((now - q.t) / 1000), z: (e.position.z - q.z) / ((now - q.t) / 1000) };
    prev.set(e.id, { x: e.position.x, z: e.position.z, t: now });
  }
  if (prev.size > 200) for (const [id, q] of prev) if (now - q.t > 5000) prev.delete(id);
}
// Who an aim hits. aim: { e (an entity aimed at), p (the point or the direction's point), len (a line's length) }.
function hitsOf(sh, me, aim, pool) {
  const R = OK_ENT_R;
  switch (sh.kind) {
    case 'single': return aim.e ? [aim.e] : [];
    case 'splash': return aim.e ? pool.filter(e => e === aim.e || dist(e.position, aim.e.position) <= sh.r + R) : [];
    case 'chain': {
      if (!aim.e) return [];
      const out = [aim.e];
      let last = aim.e;
      while (out.length < Math.max(1, sh.hops)) {
        const next = pool.filter(e => !out.includes(e) && dist(e.position, last.position) <= sh.hop + R).sort((a, b) => dist(a.position, last.position) - dist(b.position, last.position))[0];
        if (!next) break;
        out.push(next); last = next;
      }
      return out;
    }
    case 'line': {
      const dx = aim.p.x - me.x, dz = aim.p.z - me.z, l = Math.hypot(dx, dz);
      if (l < 0.01) return [];
      const ux = dx / l, uz = dz / l, len = aim.len || sh.len || l;
      let hits = pool.map(e => {
        const vx = e.position.x - me.x, vz = e.position.z - me.z, along = vx * ux + vz * uz;
        return { e, along, off: Math.abs(vx * uz - vz * ux) };
      }).filter(h => h.along >= -R && h.along <= len + R && h.off <= sh.width / 2 + R).sort((a, b) => a.along - b.along).map(h => h.e);
      if (!sh.pierce) hits = hits.slice(0, 1);
      else if (sh.maxHits) hits = hits.slice(0, sh.maxHits);
      return hits;
    }
    case 'cone': {
      const rad = (sh.r || sh.range) + R, half = sh.angle / 2;
      const dir = Math.atan2(aim.p.z - me.z, aim.p.x - me.x);
      return pool.filter(e => {
        const d = dist(me, e.position);
        if (d > rad) return false;
        if (d < R) return true;
        let a = Math.abs(Math.atan2(e.position.z - me.z, e.position.x - me.x) - dir) * 180 / Math.PI;
        if (a > 180) a = 360 - a;
        return a <= half + Math.atan2(R, d) * 180 / Math.PI;
      });
    }
    case 'circle': return pool.filter(e => dist(ahead(e, sh.lead || OK_LEAD), aim.p) <= sh.r + R);
    case 'around': return pool.filter(e => dist(me, e.position) <= sh.r + R);
    default: return aim.e ? [aim.e] : [];
  }
}
// What a cast lands: each enemy hit min(ehp, dmg), OK_KILL of the damage for each killed. dmg unknown (null): a count
// (a boss or miniboss 3), the hp hit as the tie-break.
function aimValue(hits, dmg, isBoss = e => e.monsterType === 'Boss' || e.monsterType === 'MiniBoss') {
  const ehp = hits.reduce((s, e) => s + effHp(e), 0);
  if (dmg == null) return { value: hits.reduce((s, e) => s + (isBoss(e) ? 3 : 1), 0), landed: null, kills: null, ehp };
  let landed = 0, kills = 0;
  for (const e of hits) { const h = effHp(e); landed += Math.min(h, dmg); if (h <= dmg) kills++; }
  return { value: landed + OK_KILL * dmg * kills, landed, kills, ehp };
}
// The aims worth looking at, within reach (the skill's range; a circle's centre within it, clamped).
function aimCandidates(sh, me, pool, reach, target) {
  const inR = pool.filter(e => dist(me, e.position) <= reach + OK_ENT_R);
  if (sh.onlyTarget) return target && inR.includes(target) ? [{ e: target, p: target.position }] : [];
  switch (sh.kind) {
    case 'single': case 'splash': case 'chain': return inR.map(e => ({ e, p: e.position }));
    case 'line': return inR.map(e => ({ e, p: e.position, len: Math.max(sh.len || 0, dist(me, e.position)) }));
    case 'cone': {
      const out = inR.map(e => ({ e, p: e.position }));
      for (const e of inR) {
        const near = inR.filter(x => dist(x.position, e.position) <= 3);
        if (near.length > 1) out.push({ p: { x: near.reduce((s, x) => s + x.position.x, 0) / near.length, z: near.reduce((s, x) => s + x.position.z, 0) / near.length } });
      }
      return out;
    }
    case 'circle': {
      const lead = sh.lead || OK_LEAD, out = [];
      const far = pool.filter(e => dist(me, e.position) <= reach + sh.r + OK_ENT_R);
      for (const e of far) {
        const p0 = ahead(e, lead);
        const near = far.filter(x => dist(ahead(x, lead), p0) <= 2 * sh.r);
        const cs = [p0];
        if (near.length > 1) cs.push({ x: near.reduce((s, x) => s + ahead(x, lead).x, 0) / near.length, z: near.reduce((s, x) => s + ahead(x, lead).z, 0) / near.length });
        for (let c of cs) {
          const dc = dist(me, c);
          if (dc > reach) { if (reach <= 0) continue; c = { x: me.x + (c.x - me.x) * reach / dc, z: me.z + (c.z - me.z) * reach / dc }; }
          out.push({ p: c });
        }
      }
      return out;
    }
    case 'around': return inR.some(e => dist(me, e.position) <= sh.r + OK_ENT_R) ? [{ p: me }] : [];
    default: return target && inR.includes(target) ? [{ e: target, p: target.position }] : [];
  }
}
// Lexicographic: the value, the hp hit, a circle's margin (its hits nearest its centre), the fight's target, the nearer.
function betterAim(a, b, target) {
  const E = 1e-6;
  if (Math.abs(a.value - b.value) > E) return a.value > b.value;
  if (Math.abs(a.ehp - b.ehp) > E) return a.ehp > b.ehp;
  if (Math.abs((a.spread || 0) - (b.spread || 0)) > 0.05) return (a.spread || 0) < (b.spread || 0);
  const at = !!target && a.e === target, bt = !!target && b.e === target;
  if (at !== bt) return at;
  return a.d < b.d - E;
}
// The best aim: the most value, then the most hp hit, then the target, then the nearest. null when none reaches anyone.
function bestAim(sh, me, pool, dmg, reach, target) {
  let best = null;
  for (const a of aimCandidates(sh, me, pool, reach, target)) {
    const hits = hitsOf(sh, me, a, pool);
    if (!hits.length) continue;
    const v = aimValue(hits, dmg);
    const cand = { ...a, hits, ...v, d: dist(me, a.p), spread: sh.kind === 'circle' ? hits.reduce((s, e) => s + dist(ahead(e, sh.lead || OK_LEAD), a.p), 0) / hits.length : 0 };
    if (!best || betterAim(cand, best, target)) best = cand;
  }
  return best;
}
// The decision for one ready skill. o: { sh, dmg (per enemy hit, or null), big, me, pool (can be hurt now), seen (every
// enemy in sight), reach, target, heldFor (s this skill has been held) }. -> { cast, best, why, for }.
// 'self'/'unknown' areas: { cast: true, best: null } - the caller casts as before, at its target.
function castPolicy(o) {
  const { sh, dmg, big, me, pool, seen = [], reach, target, heldFor = 0 } = o;
  if (!sh || sh.kind === 'self' || sh.kind === 'unknown') return { cast: true, best: null, why: 'as before' };
  // A cheap single-target skill keeps the fight's target while it reaches it (as before).
  if (!big && sh.kind === 'single' && target && pool.includes(target) && dist(me, target.position) <= reach + OK_ENT_R)
    return { cast: true, best: { e: target, p: target.position, hits: [target], ...aimValue([target], dmg), d: dist(me, target.position) }, why: 'cheap' };
  const best = bestAim(sh, me, pool, dmg, reach, target);
  if (!best) return { cast: false, best: null, why: 'nothing in reach' };
  if (!big || dmg == null || !(dmg > 0)) return { cast: true, best, why: 'cheap' };
  if (best.value >= OK_WORTH * dmg) return { cast: true, best, why: 'worth it' };
  const hit = new Set(best.hits.map(e => e.id));
  const better = seen.filter(e => !hit.has(e.id) && effHp(e) >= OK_WORTH * dmg).sort((a, b) => effHp(b) - effHp(a))[0] || null;
  if (better && heldFor < OK_HOLD_MAX) return { cast: false, best, why: 'saving it', for: better };
  if (best.value >= OK_LAST * dmg) return { cast: true, best, why: better ? 'held long enough' : 'the best there is' };
  return { cast: false, best, why: 'too small', for: better };
}
// A big skill: its damage known and a cooldown of OK_BIG_CD s or more between its charges.
function bigSkill(k, dmg) {
  const tr = (k && k.trigger) || {};
  if (dmg == null || !(dmg > 0)) return false;
  if (/PrecisionShot/.test((k && k.type) || '')) return true;
  return (tr.maxCooldown || 0) / Math.max(1, tr.maxCharges || 1) >= OK_BIG_CD;
}
// A ScalingValue as the game writes it ("1.5ap", "20 + 0.5ad", "3lvl") at these stats: base + ad*AD + ap*AP + lvl*level.
function scalingOf(str, st, level = 1) {
  if (typeof str !== 'string' || !str.trim()) return null;
  let v = 0, any = false;
  for (const m of str.matchAll(/(-?\d+(?:\.\d+)?)\s*(ad|ap|lvl|arm|ahp|crit)?/g)) {
    any = true;
    const x = +m[1], u = m[2];
    v += u === 'ap' ? x * (st.ap || 0) : u === 'ad' ? x * (st.ad || 0) : u === 'lvl' ? x * level : u ? 0 : x;
  }
  return any ? v : null;
}
// Precision Shot's damage at a charge (0..1): ScalingValue.Lerp(damageMin, damageMax, charge) at the hero's ability power,
// the skill's level scaling (SkillDefault: +25% a level over 1, history/decomp-core/ScalingValue.cs). null without AP.
function precisionDmgAt(charge, stats, level, dcfg) {
  const ap = stats && stats.abilityPower;
  if (!(ap > 0)) return null;
  const st = { ap, ad: stats.attackDamage || 0 }, c = Math.max(0, Math.min(1, charge || 0));
  const lo = scalingOf((dcfg && dcfg['damageMin.valueString']) || P_DMG_MIN, st, level), hi = scalingOf((dcfg && dcfg['damageMax.valueString']) || P_DMG_MAX, st, level);
  if (lo == null || hi == null) return null;
  const crit = c > ((dcfg && typeof dcfg.critThreshold === 'number') ? dcfg.critThreshold : P_CRIT) ? P_CRIT_MULT : 1;
  return (lo + (hi - lo) * c) * (1 + 0.25 * Math.max(0, (level || 1) - 1)) * crit;
}
// /hero/use (the mod's MemoryUse: casts, damage, enemies reached a cast, per memory, this run): the damage one cast lands on
// each enemy, from the change since the last read, averaged with the ones before. per: type -> { perHit, n }.
function useStep(prev, per, memories) {
  for (const m of memories || []) {
    const u = m && m.use;
    if (!u || !m.memory) continue;
    const p = prev.get(m.memory);
    prev.set(m.memory, { casts: u.casts || 0, damage: u.damage || 0 });
    if (!p) continue;
    const dc = (u.casts || 0) - p.casts, dd = (u.damage || 0) - p.damage;
    if (dc < 1 || dd < 0) continue;
    const x = dd / dc / Math.max(1, u.targetsPerCast || 1);
    const q = per.get(m.memory);
    per.set(m.memory, q ? { perHit: 0.5 * q.perHit + 0.5 * x, n: q.n + dc } : { perHit: x, n: dc });
  }
}
// ----- end of iteration 32's pure part ---------------------------------------------------------------------------------

// Each worn skill's castMethod (the indicator: _radius, _angle, _length, _width, _range), read once per type by any
// instance's id - pure reads like learnLock's. shapeCM: type -> { radius, angle, length, width, range } (or {} unread).
const shapeCM = new Map(), shapePending = new Set(), shapeSaid = new Set();
async function learnShape(id, type) {
  if (!id || !type || shapeCM.has(type) || shapePending.has(type)) return;
  shapePending.add(type);
  try {
    const base = '#' + id + '.currentConfig.castMethod.';
    const names = ['_radius', '_angle', '_length', '_width', '_range'];
    const vals = await Promise.all(names.map(n => peek(base + n)));
    const cm = {};
    names.forEach((n, i) => { if (typeof vals[i] === 'number') cm[n.slice(1)] = vals[i]; });
    shapeCM.set(type, cm);
  } finally { shapePending.delete(type); }
}
// Said once per type: the area the casts are aimed with, and where it came from.
function sayShape(sh, k) {
  if (!sh || shapeSaid.has(sh.type) || (!shapeCM.has(sh.type) && !SKILL_AREA[sh.type])) return;
  shapeSaid.add(sh.type);
  const what = sh.kind === 'line' ? `a line ${sh.width} m wide${sh.pierce ? ', piercing' : ''}${sh.maxHits ? ` (${sh.maxHits} at most)` : ''}` :
    sh.kind === 'cone' ? `a cone of ${sh.angle} deg to ${(sh.r || sh.range).toFixed(1)} m` : sh.kind === 'circle' ? `a circle of ${sh.r} m at a point within ${sh.range} m` :
    sh.kind === 'splash' ? `its target and ${sh.r} m round it` : sh.kind === 'chain' ? `a chain of ${sh.hops} jumps of ${sh.hop} m` :
    sh.kind === 'around' ? `${sh.r} m round the hero` : sh.kind === 'single' ? 'one enemy' : sh.kind === 'self' ? 'no aim (self)' : 'unknown - cast as before';
  log(`  skill area: ${sh.type} (aim ${(k.trigger || {}).aim}) ${what} [${sh.src}; castMethod ${JSON.stringify(shapeCM.get(sh.type) || null)}]`);
}
// /hero/use every USE_READ ms in fights (not waited for); "no route" (a build without it) or six failures: not asked again.
const USE_READ = 3000;
const skillUse = { at: 0, fails: 0, off: false, prev: new Map(), per: new Map() };
async function readUse() {
  if (skillUse.off || Date.now() - skillUse.at < USE_READ) return;
  skillUse.at = Date.now();
  try {
    const r = await get('/hero/use');
    skillUse.fails = 0;
    useStep(skillUse.prev, skillUse.per, r && r.memories);
  } catch (e) {
    if (/no route/i.test(String(e.message || '')) ? (skillUse.fails = 3) : ++skillUse.fails >= 6) { skillUse.off = true; log(`  /hero/use: not there (${String(e.message || e).slice(0, 60)}) - a skill's damage from its numbers alone (Precision Shot), the rest cast as before`); }
  }
}
// A skill's damage to each enemy it hits at a charge: Precision Shot by its numbers, the rest by /hero/use. null: unknown.
function skillDmg(k, hero, charge) {
  if (/PrecisionShot/.test(k.type || '')) return precisionDmgAt(charge, hero && hero.stats, k.level, precision.dmg);
  const u = skillUse.per.get(k.type);
  return u && u.n >= 2 ? u.perHit : null;
}

// The holds and casts as weighed (fight()'s okHold: slot -> { since, lastAt, type, said }; okStat: type -> the big skill's
// casts, damage landed and sent, seconds held). A hold not renewed within OK_HOLD_GAP ms is over.
const OK_HOLD_GAP = 3000;
const aimDesc = (b, me) => !b ? '-' : b.hits.length === 1 ? `${b.hits[0].name} ${Math.round(effHp(b.hits[0]))}hp @${dist(me, b.hits[0].position).toFixed(1)}m` :
  `${b.hits.length} enemies (hp ${b.hits.map(e => Math.round(effHp(e))).join('/')})`;
const okStatOf = (okStat, s, type) => { let st = okStat.get(type); if (!st) okStat.set(type, st = { slot: s, casts: 0, landed: 0, dmg: 0, heldMs: 0, holds: 0 }); return st; };
function noteHold(okHold, okStat, s, k, okd, dmg, now, hero) {
  let h = okHold.get(s);
  if (h && (h.type !== k.type || now - h.lastAt > OK_HOLD_GAP)) h = null;
  if (!h) { h = { since: now, lastAt: now, type: k.type, said: false }; okHold.set(s, h); }
  const st = okStatOf(okStat, s, k.type);
  st.heldMs += Math.min(1000, now - h.lastAt); h.lastAt = now;
  if (h.said) return;
  h.said = true; st.holds++;
  const b = okd.best;
  log(`  ${s} ${k.type} held (${okd.why}): the best now ${aimDesc(b, hero.position)} would take ~${Math.round(b.landed || 0)} of ~${Math.round(dmg)}${okd.for ? ` - kept for ${okd.for.name} ${Math.round(effHp(okd.for))}hp @${dist(hero.position, okd.for.position).toFixed(1)}m` : ''} | hp ${Math.round(hero.hp)}`);
}
function noteCast(okHold, okStat, s, k, okd, dmg, now, hero, big) {
  const b = okd.best, h = okHold.get(s);
  const v = dmg != null ? aimValue(b.hits, dmg) : null;
  const held = h && h.type === k.type && now - h.lastAt <= OK_HOLD_GAP ? (now - h.since) / 1000 : 0;
  okHold.delete(s);
  if (big && v) { const st = okStatOf(okStat, s, k.type); st.casts++; st.landed += v.landed; st.dmg += dmg; }
  if (b.hits.length >= 2) log(`  aoe: ${k.type} at (${b.p.x.toFixed(1)}, ${b.p.z.toFixed(1)}) - hits ${b.hits.length} (hp ${b.hits.map(e => Math.round(effHp(e))).join('/')})${v ? `, kills ${v.kills}, lands ~${Math.round(v.landed)}` : ''}${held >= 0.5 ? ` - after holding ${held.toFixed(1)}s` : ''}`);
  else if (held >= 0.5) log(`  ${s} ${k.type}: at ${aimDesc(b, hero.position)} after holding ${held.toFixed(1)}s (${okd.why})${v ? `, lands ~${Math.round(v.landed)} of ~${Math.round(dmg)}` : ''}`);
}
function okSummary(okStat) {
  const parts = [...okStat.entries()].map(([type, st]) => `${st.slot} ${type} ${st.casts} cast${st.casts === 1 ? '' : 's'}${st.dmg ? `, lands ~${Math.round(st.landed)} of ~${Math.round(st.dmg)} (${Math.round(100 * st.landed / st.dmg)}%)` : ''}${st.heldMs ? `, held ${(st.heldMs / 1000).toFixed(1)}s (${st.holds}x)` : ''}`);
  return `  big skills: ${parts.join('; ')}`;
}

// Boss intros. From entering a boss room to the boss in sight: run-010 17 s (Forest), 9 s (DarkCave),
// 12 s (Sky: 5 s to the entry, then ~7 s of cutscene - run-008's incident: uiState "Cutscene" ~7 s,
// no hero input). The fight loop slept through them. The game lets a player skip a cutscene - its
// skip button (DewCutsceneDirector.enableSkip; history/decomp-core/DewCutsceneDirector.cs: CmdSkip ->
// RpcSkip fades out, ends the spawn animations and jumps the timeline to its end) - and the mod's
// /cutscene/skip is that button's command. Pressed as soon as uiState says "Cutscene", again every
// 1.5 s while it lasts (the skip is refused before the timeline plays and near its end); how long it
// lasted is logged. Mid-fight cutscenes (Nyx's Erebos phase) too.
const cutscene = { since: 0, tries: 0, lastTry: 0, answer: null };
async function skipCutscene(st) {
  const now = Date.now();
  if (!cutscene.since) cutscene.since = now;
  if (now - cutscene.lastTry < 1500 || cutscene.tries >= 6) return;
  cutscene.lastTry = now; cutscene.tries++;
  const r = await tryPost('/cutscene/skip');
  // Where the hero stood when it began, against the room's combat areas: whether the walk to the intro
  // (Forest/DarkCave/Snow boss rooms: 7-9 s) could stop sooner (iteration 12).
  const me = st && st.hero && st.hero.position, room = st && st.room || {};
  const near = me ? (room.combatAreas || []).map(c => dist(me, c.position || c)).sort((a, b) => a - b)[0] : undefined;
  if (cutscene.tries === 1) log(`  cutscene: skipping it${r && r.error ? ' - ' + r.error : ''}${me ? ` - hero at (${me.x.toFixed(1)}, ${me.z.toFixed(1)})${near != null ? `, the nearest combat area ${near.toFixed(1)}m` : ''}` : ''}`);
  cutscene.answer = r && r.error ? r.error : 'ok';
}
function cutsceneOver() {
  if (!cutscene.since) return;
  log(`  cutscene: over after ${((Date.now() - cutscene.since) / 1000).toFixed(1)}s (${cutscene.tries} skip${cutscene.tries === 1 ? '' : 's'}, last answer ${cutscene.answer})`);
  cutscene.since = 0; cutscene.tries = 0; cutscene.lastTry = 0; cutscene.answer = null;
}

// A room whose clearing waits for the hero to walk into one of its parts (/state clearsOnEnter:
// RoomSection.clearRoomOnEnterFirstTime, decompiled history/decomp-core/RoomSection.cs - the room
// clears when a hero *enters* that part's polygon, ~5 m round its point, for the first time).
// run-010's zone-1 start room (Room_DarkCave_Start_0): the hero stood 0.3 m from the point for 40 s,
// `cleared` false, and the bot gave up "stuck" (the exit shut, the map would not open). The monitor
// walked it ~10 m off and back and the room cleared at once - the entering is what counts, and the
// hero had been in the part already (the entry did not register: arriving, section triggering is
// off while the room loads). Runs 001/002/007/009 cleared the same room within 4 s.
// So: on the point COE_WAIT with the room not cleared, walk to a cell COE_OFF m off it (/nav/grid,
// reachable, farthest-first near that distance), then the quiet loop walks back onto it. Up to
// COE_TRIES times, further the last two.
const COE_NEAR = 2.5, COE_WAIT = 3000, COE_OFF = 10, COE_TRIES = 4;
// The cell to step off to: reachable, with room, as near `want` m from the point as can be, and a
// short walk (pure; tests/stepoff.test.mjs).
function stepOffCell(grid, point, want) {
  const { origin, step, size, reach, clear } = grid;
  let best = null;
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || clear[k] < 1) continue;
    const p = { x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step };
    const d = dist(p, point);
    if (d < want - 3) continue;
    const score = -Math.abs(d - want) - 0.05 * reach[k] + 0.2 * Math.min(clear[k], 3);
    if (!best || score > best.score) best = { p, d, reach: reach[k], score };
  }
  return best;
}
// Iteration 20: into the part's polygon, not onto its point. Room_DarkCave_Start_0 took 25-30 s in run-010, 015 and
// 029 (3 step-offs, 10.2 / 10.2 / 13.6 m every time) and 4 s in the other 7 visits. coeWhy's read in run-029: "part 0:
// entered false, heroes in it 0", polygon (local) [(10.3,1.29), (12.95,5.19), (3.22,12.48), (-7.02,8.14), (-0.71,0.73),
// (5.09,-3.96)] - the section's own origin (0,0), the point /state lists (pathablePivot), lies 0.12 m OUTSIDE its edge
// (-0.71,0.73)-(5.09,-3.96). Decompiled (history/it16/full-core/RoomSection.cs, history/DewCollider.cs): the part is a
// PolygonCollider2D trigger over `vectors`, placed at (position.x, position.z), turned by -eulerAngles.y and scaled by
// lossyScale (x, z) (DewCollider.PositionColliders); a hero's first entry into it (a physics trigger) starts the clear.
// The hero stopping at the point from the outside never touches it; a step-off whose way back crosses the polygon
// does (the 3rd, 13.6 m). So: the polygon in the world (coePolygon), and the walk goes to a point well inside it
// (coeInside) - the step-off stays as the fallback. Pure (tests/iter20.test.mjs).
function coePolygon(pos, yawDeg, scale, vecs) {
  const y = (yawDeg || 0) * Math.PI / 180, c = Math.cos(y), s = Math.sin(y);
  const sx = scale && typeof scale.x === 'number' ? scale.x : 1, sz = scale && typeof scale.z === 'number' ? scale.z : 1;
  return vecs.map(v => { const a = v.x * sx, b = v.y * sz; return { x: pos.x + a * c + b * s, z: pos.z - a * s + b * c }; });
}
function inPoly(p, poly) {
  let inside = false;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
    const a = poly[i], b = poly[j];
    if ((a.z > p.z) !== (b.z > p.z) && p.x < (b.x - a.x) * (p.z - a.z) / (b.z - a.z) + a.x) inside = !inside;
  }
  return inside;
}
// How far inside the polygon (negative: outside) - the distance to its nearest edge.
function polyDepth(p, poly) {
  let e = Infinity;
  for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) e = Math.min(e, segDist(p, poly[j], poly[i]));
  return inPoly(p, poly) ? e : -e;
}
// Points well inside it (>= COE_DEPTH from every edge), nearest the hero first: its centroid, each corner pulled toward
// the centroid, and the middles between those; the hero's own spot projected in, too.
const COE_DEPTH = 1.5;
function coeInside(poly, me) {
  const n = poly.length;
  let cx = 0, cz = 0;
  for (const p of poly) { cx += p.x / n; cz += p.z / n; }
  const cen = { x: cx, z: cz }, cands = [cen];
  for (const p of poly) for (const f of [0.35, 0.55, 0.75]) cands.push({ x: p.x + (cen.x - p.x) * f, z: p.z + (cen.z - p.z) * f });
  for (let i = 0; i < n; i++) { const a = poly[i], b = poly[(i + 1) % n]; for (const f of [0.35, 0.6]) cands.push({ x: (a.x + b.x) / 2 + (cen.x - (a.x + b.x) / 2) * f, z: (a.z + b.z) / 2 + (cen.z - (a.z + b.z) / 2) * f }); }
  const md = Math.hypot(me.x - cen.x, me.z - cen.z);
  if (md > 0.1) for (let k = 1; k <= 12; k++) { const t = k / 12; cands.push({ x: me.x + (cen.x - me.x) * t, z: me.z + (cen.z - me.z) * t }); }
  return cands.map(p => ({ p, depth: polyDepth(p, poly) })).filter(c => c.depth >= COE_DEPTH)
    .sort((a, b) => Math.hypot(me.x - a.p.x, me.z - a.p.z) - Math.hypot(me.x - b.p.x, me.z - b.p.z));
}
// The part behind a clears-on-enter point, read once per point (pure reads): its polygon in the world and the points
// inside it to walk to. Null when it cannot be read (the step-off then works as before).
const coeParts = new Map();
async function coePart(point, me) {
  const key = Math.round(point.x) + ':' + Math.round(point.z);
  if (coeParts.has(key)) return coeParts.get(key);
  coeParts.set(key, null);
  for (let i = 0; i < 12; i++) {
    const base = `Room.instance.sections[${i}]`;
    const flag = await peek(base + '.clearRoomOnEnterFirstTime');
    if (flag === undefined) break;
    if (flag !== true) continue;
    const [pos, eul, sc, vecs] = await Promise.all([peek(base + '.transform.position'), peek(base + '.transform.eulerAngles'), peek(base + '.transform.lossyScale'), peek(base + '.vectors')]);
    if (!pos || !Array.isArray(vecs) || vecs.length < 3 || Math.hypot(pos.x - point.x, pos.z - point.z) > 1.5) continue;
    const poly = coePolygon(pos, eul && eul.y, sc, vecs);
    const inside = coeInside(poly, me);
    const part = { i, poly, inside, pivotDepth: polyDepth(point, poly) };
    coeParts.set(key, part);
    log(`  clears-on-enter part ${i}: turned ${eul ? eul.y.toFixed(0) : '?'} deg, scale ${sc ? sc.x.toFixed(2) + 'x' + sc.z.toFixed(2) : '?'}; its point (${point.x.toFixed(1)}, ${point.z.toFixed(1)}) ${part.pivotDepth >= 0 ? `${part.pivotDepth.toFixed(2)} m inside` : `${(-part.pivotDepth).toFixed(2)} m OUTSIDE`} its polygon [${poly.map(p => p.x.toFixed(1) + ',' + p.z.toFixed(1)).join(' ')}]; ${inside.length ? `walking to (${inside[0].p.x.toFixed(1)}, ${inside[0].p.z.toFixed(1)}), ${inside[0].depth.toFixed(1)} m inside` : 'no point well inside it - the point itself'}`);
    return part;
  }
  return null;
}
// Why the room has not cleared, by pure reads (iteration 12; run-015 needed 3 step-offs, 25 s, where
// runs 001/002/007/009 took 4 s): decompiled, entering the part only starts RoomSection's clear, which
// first waits for the aggroed enemies to die (waitForAggroedEnemiesToDie); an entry while section
// triggering is off (arriving) is held until it is on again; and the part is a polygon (`vectors`) the
// point (its pivot) need not lie in. So: the hero's isSectionTriggeringDisabled, a zone transition,
// and per clears-on-enter part whether it saw its first entry, the heroes in it, its waiting rule.
async function coeWhy() {
  const idx = [...Array(12).keys()];
  const [trig, trans, ...flags] = await Promise.all([peek('$hero.Status.isSectionTriggeringDisabled'), peek('ZoneManager.instance.isInAnyTransition'),
    ...idx.map(i => peek(`Room.instance.sections[${i}].clearRoomOnEnterFirstTime`))]);
  const parts = [];
  for (const i of idx) if (flags[i] === true) {
    const base = `Room.instance.sections[${i}]`;
    const [first, heroes, wait, pos, vecs] = await Promise.all([peek(base + '._didInvokeOnEnterFirstTime'), peek(base + '.numOfHeroes'), peek(base + '.waitForAggroedEnemiesToDie'), peek(base + '.transform.position'), peek(base + '.vectors')]);
    parts.push(`part ${i}: entered ${first}, heroes in it ${heroes}, waits for aggroed ${wait}, at ${pos ? pos.x.toFixed(1) + ',' + pos.z.toFixed(1) : '?'}, polygon ${JSON.stringify(vecs)}`);
  }
  return `triggering off ${trig}, in transition ${trans}; ${parts.join('; ') || 'no part read'}`;
}
// Called on each quiet look while the goal is a clears-on-enter point; true when it stepped off.
async function stepOff(coe, me, point, what = 'clears-on-enter point') {
  const key = Math.round(point.x) + ':' + Math.round(point.z);
  if (coe.key !== key) { coe.key = key; coe.since = 0; coe.tries = 0; }
  if (dist(me, point) > COE_NEAR) { coe.since = 0; return false; }
  if (!coe.since) { coe.since = Date.now(); return false; }
  if (Date.now() - coe.since < COE_WAIT || coe.tries >= COE_TRIES) return false;
  coe.tries++; coe.since = 0;
  const want = coe.tries <= 2 ? COE_OFF : COE_OFF + 5;
  let grid = null;
  try { grid = await get('/nav/grid', { radius: want + 3, step: 1 }); } catch { }
  const cell = grid && stepOffCell(grid, point, want);
  if (!cell) { log(`  ${what} (${point.x.toFixed(1)}, ${point.z.toFixed(1)}): not cleared, and nowhere ${want} m off to step to`); return false; }
  log(`  ${what} (${point.x.toFixed(1)}, ${point.z.toFixed(1)}): stood on it ${(COE_WAIT / 1000).toFixed(0)}s, the room not cleared - stepping ${cell.d.toFixed(1)}m off and back (try ${coe.tries})`);
  if (coe.tries <= 2 && what === 'clears-on-enter point') log(`  clears-on-enter: ${await coeWhy().catch(e => 'read failed: ' + e.message)}`);
  await tryPost('/hero/move', { x: cell.p.x, z: cell.p.z, wait: true, timeout: 6 });
  return true;
}

// Iteration 52 (run-055's Room_Ink_Combat_2): a goal left as unreachable (noPath: the move ended short of it) was left for good
// until a teleport. The room's west combat area (-27, 17) had no path as the hero came in (17.8 m short); after the first wave
// the way was open (the monitor's /nav/path: complete, 26.9 m), but the bot never asked again: the shrine and the exit across
// the river, 40 s quiet, "quiet but exit closed", stuck. Now each such goal is asked again with /nav/path (a pure read; its
// destination is the move's own clamp, Dew.GetValidAgentDestination_Closest) NOPATH_RECHECK after the last ask, and at once
// after the room changed (a fight since, a combat area woken, the exit or the clear). Reachable again, it is a goal again and
// the quiet clock gets QUIET_REOPEN_MS for the walk (NOPATH_REOPEN_MAX times a goal). A lava refusal is not asked again
// (iteration 36's stall rule has it), nor a goal that came back and failed NOPATH_REOPEN_MAX times.
const NOPATH_RECHECK = 4000, NOPATH_NEAR = 4, NOPATH_REOPEN_MAX = 2, QUIET_GIVE_UP = 40000, QUIET_REOPEN_MS = 20000;
// The noPath entries to ask about now (oldest ask first): key -> { p, at (last ask or mark), first, why }.
function noPathDue(noPath, now, changedAt, every = NOPATH_RECHECK) {
  return [...noPath.entries()].filter(([, e]) => e && e.p && e.why === 'move' && (e.at < changedAt || now - e.at >= every)).sort((x, y) => x[1].at - y[1].at);
}
// /nav/path's answer for a goal: reachable when the path is found whole and ends within `near` of the goal (the move's test).
function pathReaches(np, goal, near = NOPATH_NEAR) {
  if (!np || !np.destination || typeof np.destination.x !== 'number') return false;
  if (np.status && np.status !== 'PathComplete') return false;
  return dist(np.destination, goal) <= near;
}
// What the room looks like to the quiet branch: a change (a combat area woken or done, the exit, the clear) is asked about at once.
const roomSig = room => (room ? [(room.combatAreas || []).map(c => (c.active ? 'a' : 'i')).join(''), (room.clearsOnEnter || []).length, room.exitOpen ? 1 : 0, room.cleared === true ? 1 : 0].join('|') : '');
// Iteration 52: 'quiet but exit closed' in a room that is neither cleared nor open - fight() again (its goals all asked anew)
// before the exit and "stuck": every such end in runs 031-055 (031, 045, 046 x4, 055) was a stuck run.
const QUIET_RETRIES = 2;
const quietRetry = (result, room, n, isBossRoom, finalZone) => result === 'quiet but exit closed' && !isBossRoom && !finalZone && !!room && room.exitOpen !== true && room.cleared !== true && n < QUIET_RETRIES;

async function fight(maxSeconds = 300) {
  const started = Date.now();
  // Goals /hero/move cannot reach (the quiet branch): key -> { p, at, first, why: 'move' | 'lava' | 'given up' }; cleared by a
  // teleport; the 'move' ones asked again (iteration 52: noPathDue).
  const noPath = new Map();
  const reopened = new Map();   // key -> times a noPath goal came back (iteration 52)
  let roomChangedAt = 0, lastRoomSig = null;
  const markNoPath = (k, p, why) => { const now = Date.now(); noPath.set(k, { p: { x: p.x, z: p.z }, at: now, first: now, why: why === 'move' && (reopened.get(k) || 0) >= NOPATH_REOPEN_MAX ? 'given up' : why }); };
  let lastQuietPos = null;
  let quietSince = 0, lastLog = 0, sawEnemy = false;
  let lastDir = { x: 0, z: 0 }, lastDirAt = 0, lastPathAt = 0, gridCache = null, gridAt = 0;
  let lastNearbyLoot = 0, propsCache = [], propsAt = 0, lastBossD = null, lastNear = null;
  let stuckFrom = null, stuckAt = 0, heldLogAt = 0, dmgAt = 0, bhDashAt = 0;
  const bhTrack = [];   // the Blackhole: { t, d } of the last looks (the radial speed), .on, .openAt
  // Untimed blobs by where they are: when each was first seen, for the trace (their age against
  // the hits tells how long a blob takes to land - run-003's Skoll swords: ~1.1 s).
  const blobSeen = new Map();
  const strikesSeen = new Map();   // readAreas: recent strikes, whose drawings (blobs) are dropped
  const coe = { key: null, since: 0, tries: 0 };   // stepOff: the clears-on-enter point stood on
  coeParts.clear();   // coePart: read again in each room
  const pStat = { n: 0, dMax: 0, cSum: 0 };   // Precision Shot casts this fight (iteration 20)
  // Iteration 32: the enemies' pace (notePace), each slot's hold (since, said), each big skill's casts this fight (okStat).
  const paceSeen = new Map(), okHold = new Map(), okStat = new Map();
  const lavaHold = { stickId: null, id: null, since: 0, said: null };   // stickyTarget, and the edge held for one across the lava (iteration 33)
  const breath = { t0: 0, last: 0, facing: null, side: null, dashAt: 0, said: false };   // Infernus's breath (iteration 33)
  const spinSt = { said: null, dasherSaid: null, dashAt: 0, noCellSaid: false };   // spinning arrows and dashers (iteration 41)
  // Iteration 46: Primus's Doom (its meteors, the place to wait - doomNote / doomSpot) and its Adapt Atk's clock (adaptNote).
  const doomSt = { seen: new Map(), list: [], from: null, spot: null, saidAt: 0, checked: new Set(), n: 0 };
  const adaptSt = {};
  let capSaidAt = 0;
  // Iteration 44: pinned (pinStep's since/stalls), the break-out under way (on), the wide grid it plans on, this fight's tally.
  const pin = { since: 0, stalls: [], on: null, big: null, bigAt: 0, coolUntil: 0, dashAt: 0, replan: false, n: 0, secs: 0, took: 0, noneSaidAt: 0 };
  let orbSaid = false, poolSaid = null, quietPool = null, fanSaidAt = 0, lobSaid = false, quietRed = null, walkRedSaid = null;
  const azSt = {}, azSaid = { windup: 0, trail: false };   // iteration 50: Azurak's Atk (azurakAtk)
  const unl = { list: null, readAt: 0, target: null, stopped: false };   // iteration 38: the reward shrine waited at before the clear (clearGoal)
  const caStep = { key: null, since: 0, tries: 0 };   // stepOff for an inactive combat area stood in (iteration 21)
  const orbSeen = new Map();   // chaser orb id -> when first seen (its 4 s life; ORB_LATE)
  const hpWatch = { hp: null, total: 0, drop: null };   // hp lost with no hit in /damage (iteration 13)
  const lava = { since: 0, last: 0, from: 0, total: 0, dashAt: 0, said: false, big: null, bigAt: 0 };   // a stay on the lava (iteration 14)
  const quietLava = {};   // lavaStep's state for the quiet walks (iteration 17)
  const spot = { id: null, at: 0, s: null, g: null, said: null, hopAt: 0 };   // shootSpot for a pillar out of reach (iteration 17)
  let meteorAt = 0;   // the last Infernus meteor seen (iteration 17)
  const dfa = { first: 0, last: 0, said: 0 };   // skollLanding: Death From Above's follow drawing (iteration 18)
  const castBusy = { p: null, since: 0 };   // castQuick: a skill cast whose reply has not come yet (iteration 18)
  const moveHist = [];   // walkSpeed: { t, x, z, walking } of the last looks (iteration 19)
  let folSaidAt = 0;   // followMove: when "it follows the hero" was last said (iteration 19)
  // Iteration 26: the zone boss's immunity (readImmunity), its phase (bossPhase: since when, what was said), the frozen-hp
  // fallback (frozenStep) and the last basic attack sent (at whom, when).
  const imm = { id: null, readAt: 0, immune: null, untargetable: null, effects: null, fxAt: 0, fails: 0, via: null,
    phase: null, phaseId: null, since: 0, preSaid: false, atkSaid: null, shieldSaid: false, armorSaid: false };
  const frz = {};
  let atkAt = 0, atkId = null;
  const stopDir = async () => { if (lastDir.x || lastDir.z) { await tryPost('/hero/move_dir', { x: 0, z: 0 }); lastDir = { x: 0, z: 0 }; } };
  while ((Date.now() - started) / 1000 < maxSeconds) {
    const [st, ents, hero, threats] = await Promise.all([
      get('/state'),
      get('/entities', { kind: 'enemies', radius: 35, limit: 30 }),
      get('/hero').catch(() => null),
      get('/threats', { radius: 25 }).catch(() => ({ projectiles: [] })),
    ]);
    if (st.uiState === 'Result') return finalDown ? 'final boss down' : 'dead';
    // Iteration 27: Primus down (finalBossGone) - the ending follows, not a room to clear.
    if (finalBossGone(st.room, bossSeenHere)) { await stopDir(); return 'final boss down'; }
    if (!st.hero || !hero) { await sleep(300); continue; }
    tallyTick(hero, ((ents && ents.entities) || []).length > 0);   // iteration 29: casts a fight minute per memory
    if (st.hero.knockedOut) return 'dead';
    if (st.hero.holding) await sortHands();
    if (await handleBlocking(st)) { await sleep(200); continue; }
    if (st.uiState === 'Cutscene') { await skipCutscene(st); await sleep(300); continue; }
    cutsceneOver();
    if (st.loading || st.uiState !== 'Playing') { await sleep(300); continue; }
    // Iteration 49: a build before readers 49 lists White Night's Wave as a 0.71 m slice (its polygon's unused box size) - dropped;
    // the bot reads it itself (waveRead).
    if (Array.isArray(threats.areas) && !(typeof threats.readers === 'number' && threats.readers >= WAVE_READERS)) threats.areas = threats.areas.filter(a => !a || a.type !== WAVE_TYPE);
    notePools(pools, { areas: threats.areas || [], now: Date.now() });
    // Iteration 43: the Ink ground no longer listed by /threats (it listed it before) - destroyed (groundGone).
    if (st.hero.position && groundGone(pools, threats.areas, st.hero.position, Date.now())) log(`  damaging ground: no longer listed - gone`);
    if (Date.now() - dmgAt > 400) { dmgAt = Date.now(); await readHits(); }

    const entities = ents.entities;
    const room = st.room || {};
    if (entities.length === 0) {
      if (room.enemiesAlive === 0) {
        if (!quietSince) quietSince = Date.now();
        // Rooms have several combat areas that wake as the hero walks in, so only the open exit
        // says the room is done.
        // Iteration 21: a quiet walk still under way (/hero/move) is stopped too - run-030's Combat_0_2 cleared while the
        // walk to the next part of the room went on over the lava's edge and through a Magmadon pool (the burn relit).
        if (room.exitOpen && unl.target) { const me1 = st.hero.position; log(`  the room cleared ${quietSince ? ((Date.now() - quietSince) / 1000).toFixed(1) : '?'}s after the last enemy - the hero ${me1 ? dist(me1, unl.target.position).toFixed(1) : '?'}m from ${unl.target.type}`); unl.target = null; }
        if (room.exitOpen) { await stopDir(); if (lastQuietPos) await tryPost('/hero/stop'); await readHits(); if (groundEnds(pools, Date.now() + GROUND_GRACE)) log(`  damaging ground: the room is clear - it hurts ${(GROUND_GRACE / 1000).toFixed(1)}s more at most (its Routine: 3 s, then destroyed); the loot waits outside it`); if (Object.keys(tookAcc).length) log('  took', tookLine()); if (coe.tries) log(`  clears-on-enter: cleared after ${coe.tries} step${coe.tries > 1 ? 's' : ''} off`); if (pStat.n) log(`  precision shot: ${pStat.n} cast${pStat.n > 1 ? 's' : ''} in ${((Date.now() - started) / 1000).toFixed(0)}s, charged ${(pStat.cSum / pStat.n).toFixed(2)}s on average, the farthest @${pStat.dMax.toFixed(1)}m`); if (okStat.size) log(okSummary(okStat)); if (pin.n) log(`  pinned: ${pin.n} time${pin.n > 1 ? 's' : ''} this fight, ${pin.secs.toFixed(1)}s in all, took ${pin.took} while pinned`); return 'clear'; }
        // Iteration 27: the final zone's rooms before the boss go on by its door (Shrine_PrimusDoor), not an exit.
        if (isFinalZone(room) && room.nodeType !== 'ExitBoss' && room.cleared === true && Date.now() - quietSince > 1500) { await stopDir(); return 'clear'; }
        if (Date.now() - quietSince > QUIET_GIVE_UP) {
          const left = [...noPath.values()].filter(e => e.why !== 'lava').map(e => `(${e.p.x.toFixed(1)}, ${e.p.z.toFixed(1)})`);
          if (left.length) log(`  quiet ${(QUIET_GIVE_UP / 1000).toFixed(0)}s: still no path to ${left.join(', ')}`);
          return 'quiet but exit closed';
        }
      }
      await stopDir();
      // Iteration 43: the Ink boss room, its boss seen, nobody left alive, the ground still on with no end known (the boss down,
      // the room not clear yet) - runs 049 and 051 walked into it here for the drops and the soul (the lull's loot, the quiet
      // walk): no loot, no walk; out of it if in it, else stand until the clear (then groundEnds, and the loot waits out its
      // last 3 s in waitOutFire). Pure choice: groundHold.
      const gh = groundHold(pools, Date.now(), st.hero.position, { bossSeen: bossSeenHere, bossRoom: room.nodeType === 'ExitBoss', alive: room.enemiesAlive, quietMs: quietSince ? Date.now() - quietSince : 0 });
      if (gh) {
        if (!groundHeld.said) { groundHeld.said = true; log(`  damaging ground: the boss is down and the room not clear yet - ${gh.out ? `out of it first (${dist(st.hero.position, gh.p.centre).toFixed(1)}m from its centre, r ${gh.p.radius.toFixed(1)})` : 'standing outside it'} until the clear, no loot in it | hp ${Math.round(hero.hp)}`); }
        if (gh.out) { await tryPost('/hero/move', { x: gh.out.x, z: gh.out.z }); groundHeld.stopped = false; }
        else if (!groundHeld.stopped) { groundHeld.stopped = true; await tryPost('/hero/stop'); }
        lastQuietPos = null;
        await sleep(250);
        continue;
      }
      if (!st.hero.inCombat && Date.now() - lastNearbyLoot > 5000) {
        lootAbort = 25; lootDanger = true; try { await loot(16, 6, true); } finally { lootAbort = 0; lootDanger = false; } lastNearbyLoot = Date.now();
        // The loot walks: the next look must not read that as a teleport (bossEntryGoal, noPath).
        lastQuietPos = null; if (bossEntry) bossEntry.last = null;
        continue;
      }
      const far = (await get('/entities', { kind: 'enemies', radius: 300, limit: 1 })).entities[0];
      // No one in sight and the exit shut: a part of the room still to fight in, or the part whose
      // entering clears the room, or the exit.
      // A goal /hero/move cannot reach (it answers with a destination far from it - run-008's Sky
      // arena from the start plateau answered the hero's own spot) is left out until the hero is
      // teleported; then the next one.
      const me0 = st.hero.position;
      if (lastQuietPos && dist(me0, lastQuietPos) > 12) noPath.clear();
      lastQuietPos = me0;
      const gk = p => Math.round(p.x) + ':' + Math.round(p.z);
      // Iteration 52: the goals left as unreachable asked again - two a look at most (noPathDue, pathReaches).
      const sig = roomSig(room);
      if (lastRoomSig !== null && sig !== lastRoomSig) roomChangedAt = Date.now();
      lastRoomSig = sig;
      if (noPath.size) {
        for (const [k, e] of noPathDue(noPath, Date.now(), roomChangedAt).slice(0, 2)) {
          e.at = Date.now();
          const np = await get('/nav/path', { x: e.p.x, z: e.p.z }).catch(() => null);
          if (!pathReaches(np, e.p)) continue;
          noPath.delete(k);
          const n = (reopened.get(k) || 0) + 1;
          reopened.set(k, n);
          if (n <= NOPATH_REOPEN_MAX && quietSince) quietSince = Math.max(quietSince, Date.now() - (QUIET_GIVE_UP - QUIET_REOPEN_MS));
          log(`  the way to (${e.p.x.toFixed(1)}, ${e.p.z.toFixed(1)}) is open now (${typeof np.length === 'number' ? np.length.toFixed(1) : '?'}m walk; no path ${((Date.now() - e.first) / 1000).toFixed(0)}s ago) - a goal again`);
        }
      }
      const nearestOf = list => (list || []).map(x => x.position || x).filter(p => !noPath.has(gk(p))).sort((p, q) => dist(me0, p) - dist(me0, q))[0];
      let goal = far ? far.position : null;
      // A boss room before its boss has been seen: the boss-room entry first (bossEntryGoal).
      if (!goal && room.nodeType === 'ExitBoss' && !room.exitOpen && !bossSeenHere) goal = await bossEntryGoal(me0, room);
      const isEntry = !!goal && !far;
      // Iteration 21: an inactive combat area as the goal is kept (caGoal) - stood in, it may never wake (run-031's incident).
      const caGoal = goal ? null : nearestOf((room.combatAreas || []).filter(c => !c.active));
      goal = goal || caGoal;
      const coeGoal = goal ? null : nearestOf(room.clearsOnEnter);
      // Iteration 20: a point well inside that part's polygon (coePart), not its own point - which can lie outside it.
      let coeWalk = null;
      if (coeGoal) {
        const part = await coePart(coeGoal, me0).catch(() => null);
        const c = part && part.inside.find(x => !noPath.has(gk(x.p)));
        coeWalk = c ? c.p : null;
      }
      // Iteration 38: all fought and the room not clear yet (the game waits for the aggroed enemies first) - to the nearest reward
      // shrine not used yet, and wait beside it (clearGoal), not to the exit and back (run-045 Combat_3_4). Up to UNLOCK_CAP.
      if (!goal && !coeGoal && room.nodeType === 'Combat' && room.enemiesAlive === 0 && quietSince && Date.now() - quietSince < UNLOCK_CAP) {
        if (Date.now() - unl.readAt > 1000) { unl.readAt = Date.now(); const r = await get('/interactables', { radius: FAR_SHRINE + 5 }).catch(() => null); unl.list = r ? r.interactables : null; }
        const cg = clearGoal((unl.list || []).filter(i => i.position && !noPath.has(gk(i.position))).map(i => ({ ...i, distance: +dist(me0, i.position).toFixed(1) })), triedHere, GOOD_SHRINES, FAR_SHRINE);
        const us = unlockStep(room, Date.now() - quietSince, cg, me0);
        if (us) {
          const s = cg.target;
          if (!unl.target || unl.target.id !== s.id) { unl.target = s; unl.stopped = false; log(`  after the last enemy: ${cg.locked.length} to unlock (${cg.locked.map(i => i.type.replace(/^Shrine_/, '')).join(', ') || 'none'}) - to the nearest, ${s.type} @${s.distance}m, not the exit`); }
          if (us.act === 'wait') { if (!unl.stopped) { unl.stopped = true; await stopDir(); await tryPost('/hero/stop'); } await sleep(250); continue; }
          goal = us.p;
        }
      }
      goal = goal || coeWalk || coeGoal || (room.nodeType === 'ExitBoss' && bossEntry && bossEntry.arena ? nearestOf([bossEntry.arena]) : null) || room.exitPosition;
      // Iteration 48: the jump shrine held for a part of the room still to fight on foot (despairHop's gate) - that part first.
      // Reached, it is not offered again in this room (footDone), woken or not.
      const hf = hopState.foot;
      if (hf && isDespair(room) && Date.now() - hf.at < 20000) {
        if (dist(me0, hf.p) > 3) goal = hf.p;
        else { hopState.footDone.add(footKey(room, hf.p)); hopState.foot = null; log(`  despair: at the part still to fight (${hf.p.x.toFixed(1)}, ${hf.p.z.toFixed(1)}) | ${plural(room.enemiesAlive || 0, 'enemy', 'enemies')} alive in the room`); }
      } else if (hf) hopState.foot = null;
      // Standing on the point that should clear the room and it does not: step off and back (stepOff).
      if (coeGoal && await stepOff(coe, me0, coeWalk || coeGoal)) { lastQuietPos = null; continue; }
      // Iteration 21 (run-031's incident, Forest_Combat_0_2): the room's second combat area stayed inactive with the hero
      // 0.3 m from its point (the first wave fought and the well used on top of it) - 43 s, then "quiet but exit closed",
      // stuck; walked 6 m off by hand, it woke at once. Stood on for COE_WAIT: stepped off and back, as a clears-on-enter point.
      if (caGoal && await stepOff(caStep, me0, caGoal, 'inactive combat area')) { lastQuietPos = null; continue; }
      // Iteration 21: a burning pool across the way that goes out within QUIET_POOL_WAIT - waited for, not walked through
      // (run-031's Combat_0_1: the walk to the next part of the room crossed an ExplosionSub pool with 2.4 s left - 4 ticks
      // and the burn from 1 to 5 stacks, most of the 494 of burn in that room). Standing in one: walked on (out of it).
      if (goal) {
        const now = Date.now(), pp = poolInWay(pools, now, me0, goal);
        if (pp && !pp.fixed && dist(me0, pp.centre) >= pp.radius + 0.8 && pp.until - now < QUIET_POOL_WAIT) {
          if (quietPool !== pp) { quietPool = pp; await stopDir(); await tryPost('/hero/stop'); log(`  ${far ? 'a far enemy' : isEntry ? 'the boss-room entry' : 'the next part of the room'}: a burning pool (${pp.radius.toFixed(1)}m, ${((pp.until - now) / 1000).toFixed(1)}s more) across the way - waiting for it to go out`); }
          await sleep(200);
          continue;
        }
      }
      // Iteration 28: timed red across the first WALKUP_LOOK m of the way (a Barrage's circles from a far Dark Elemental, a strike
      // still to land) - waited out where the hero stands, or stepped out of when it lies under the hero.
      if (goal) {
        const rw = redInWay(walkReds(threats), me0, alongTo(me0, goal, WALKUP_LOOK), RED_WAIT_LEFT);
        if (rw) {
          const k = (rw.area.type || rw.area.shape) + (rw.inside ? ' in' : '');
          if (quietRed !== k) { quietRed = k; log(`  ${far ? 'a far enemy' : isEntry ? 'the boss-room entry' : 'the next part of the room'}: a ${rw.area.shape} of ${rw.area.by || rw.area.type || '?'} (${rw.area.radius} m, lands in ${rw.area.left}s) ${rw.inside ? 'under the hero - stepping out' : 'across the way - waiting for it to land'}`); }
          await stopDir();
          if (rw.inside) { const o = outOf(rw.area, me0); await tryPost('/hero/move', { x: o.x, z: o.z }); } else await tryPost('/hero/stop');
          await sleep(200);
          continue;
        }
      }
      // Iteration 17: lava on the way (LavaLand) - round it on dry ground, or a dash across it (lavaStep).
      // Iteration 24: 'refused' (a walk over the lava too long or too costly, no dash, no dry way): that goal is left for
      // the room's next one (noPath); a far enemy is waited for where the hero stands.
      const ls = goal && onLavaZone() ? await lavaStep(goal, far ? 'a far enemy' : isEntry ? 'the boss-room entry' : 'the next part of the room', quietLava) : null;
      if (ls === 'refused') { if (!far && !noPath.has(gk(goal))) markNoPath(gk(goal), goal, 'lava'); await sleep(400); continue; }
      if (ls) { await sleep(150); continue; }
      if (goal) {
        const r = await tryPost('/hero/move', { x: goal.x, z: goal.z });
        // Iteration 35: in Despair a goal the navmesh cannot reach lies on another island - over by a jump shrine (despairHop).
        const short = r && r.destination && typeof r.destination.x === 'number' ? dist(r.destination, goal) : 0;
        if (short > 4 && isDespair(room) && await despairHop(goal, far ? 'a far enemy' : isEntry ? 'the boss-room entry' : 'the next part of the room')) {
          noPath.clear(); lastQuietPos = null; if (bossEntry) bossEntry.last = null;
          await sleep(200);
          continue;
        }
        if (!far && r && r.destination && typeof r.destination.x === 'number' && dist(r.destination, goal) > (isEntry ? 8 : 4)) {
          if (isEntry) { bossEntry.done = true; log(`  boss-room entry: no path to it (the move ends ${dist(r.destination, goal).toFixed(1)}m short) - on to the room's other goals`); }
          else if (!noPath.has(gk(goal))) { markNoPath(gk(goal), goal, 'move'); log(`  no path to (${goal.x.toFixed(1)}, ${goal.z.toFixed(1)}): the move ends ${dist(r.destination, goal).toFixed(1)}m short - trying the next goal`); }
        }
        // A long quiet walk (the boss-room approach, a far part of the room): dashed along (walkDash). Not once
        // the zone boss has been seen here (a lull inside its fight: Nyx's Erebos, a teleport away).
        else if (WALK_DASHES && !st.hero.inCombat && !bossSeenHere && await walkDash(goal, hero, QUIET_DASH_LEFT, far ? 'a far enemy' : isEntry ? 'the boss-room entry' : 'the next part of the room')) {
          await tryPost('/hero/move', { x: goal.x, z: goal.z });
        }
      }
      await sleep(400);
      continue;
    }
    quietSince = 0; lastQuietPos = null; if (bossEntry) bossEntry.last = null; roomChangedAt = Date.now();   // iteration 52: noPath asked again after a fight
    if (!sawEnemy) { sawEnemy = true; if (!roomCounted) { roomCounted = true; fights++; } }

    const me = hero.position;
    const bosses = entities.filter(isBossE);
    const bossFight = bosses.some(e => e.monsterType === 'Boss');   // a zone boss, not a miniboss
    if (bossFight) bossSeenHere = true;
    // Iteration 26: who cannot be hurt now (hurtBlock) - the flag, the zone boss's immunity read by the game's own check
    // (readImmunity), the frozen-hp fallback (frozenStep, on the one the last basic attack went at).
    const zb = bosses.find(e => e.monsterType === 'Boss');
    if (zb) await readImmunity(zb, imm);
    const nowI = Date.now();
    const aRange = (hero.attack && hero.attack.range) || 8;
    if (atkId != null) {
      const e = entities.find(x => x.id === atkId);
      if (e && isBossE(e)) {
        const was = frz.id === e.id && frz.until > nowI;
        const fr = frozenStep(frz, { id: e.id, sample: e.hp + (e.shield || 0), hitting: nowI - atkAt < 350 && dist(me, e.position) <= aRange - 0.3, now: nowI });
        if (fr && !was) log(`  boss hp frozen: ${e.name} at ${Math.round(e.hp)}${e.shield ? ` + ${Math.round(e.shield)} shield` : ''} through ${FROZEN_T}s of attacks from within reach, no flag${imm.id === e.id && imm.via ? ` (the game's check: immune ${imm.immune}, untargetable ${imm.untargetable})` : ''} - no attacks or skills at it for ${FROZEN_HOLD}s (${frz.n}) | hp ${Math.round(hero.hp)}`);
      }
    }
    const blockOf = e => hurtBlock(e, e && e.id === imm.id ? imm : null, !!(e && frz.id === e.id && frz.until > nowI));
    // Iteration 35: while Azurak roars, what casts a shadow from it (a monster spawner, a pillar) is not shot (roarSafe).
    const roarNow = !!roarSafe(threats.areas);
    const cannot = e => !!blockOf(e) || (roarNow && !!e && ROAR_SHELTER.test(e.type || ''));
    // An invulnerable boss (Infernus's shield): its pillars first, however far (chooseTarget).
    const first = firstTarget(entities);
    const firstBlock = blockOf(first);
    // A short hold (the frozen-hp fallback, Untargetable alone - the Seeker's ~1 s Blink): attacks and skills wait, the hero keeps
    // the fight's range, no phase is timed, no far read.
    const shortHold = b => b === 'frozen' || b === 'untargetable';
    const shieldUp = (firstBlock && !shortHold(firstBlock) && first.monsterType === 'Boss') || shieldSign(entities);   // iteration 16: zone bosses only
    if (shieldUp && Date.now() - shield.farAt > 1000) {
      shield.farAt = Date.now();
      shield.far = ((await get('/entities', { kind: 'enemies', radius: 300, limit: 12 }).catch(() => null)) || {}).entities || [];
    }
    // A far one that should be within the 35 m read but is not there any more has died since.
    const far = shieldUp ? shield.far.filter(e => dist(me, e.position) > 33 || entities.some(x => x.id === e.id)) : [];
    const ct = chooseTarget(me, entities, far, cannot);
    // Iteration 33: in a room with no boss or miniboss the target is kept (stickyTarget) - no flip between two at like
    // distances - and one on this side of the lava goes first (run-042's Combat_0_1).
    let target = ct.target;
    if (!ct.why && !entities.some(isBossE)) {
      const open = entities.filter(e => !cannot(e) && e.alive !== false);
      const t2 = open.length ? stickyTarget(lavaHold.stickId, open, me, aRange, e => shield.lavaWay.get(e.id) === false) : null;
      if (t2) target = t2;
    }
    lavaHold.stickId = target.id;
    const d = dist(me, target.position);
    // Iteration 26: the zone boss's phase - named by its status effect (or its type and health), timed from when it began.
    const zbBlock = zb ? blockOf(zb) : null;
    if (zb && zbBlock && !shortHold(zbBlock)) {
      if (!imm.phase || imm.phaseId !== zb.id) {
        imm.phase = bossPhase(zb, imm.effects); imm.phaseId = zb.id; imm.since = nowI; imm.preSaid = false;
        log(`  boss phase: ${imm.phase.name} (${zbBlock}${imm.phase.by !== 'type' ? ', ' + imm.phase.by : ''}${imm.effects ? '; effects ' + imm.effects.filter(t => !/^Se_Star_/.test(t)).join(',') : ''}) at ${Math.round(zb.hp)}/${Math.round(zb.maxHp || 0)}${imm.phase.len ? `, ~${imm.phase.len}s expected` : ''} - no attacks or skills at it${imm.phase.pre ? `, closing to ${PRE_RANGE} m ${PRE_T}s before its end` : ''} | hp ${Math.round(hero.hp)}`);
      } else if (imm.phase.by === 'type' && imm.effects) {
        const p = bossPhase(zb, imm.effects);
        if (p.by !== 'type' && p.name !== imm.phase.name) { log(`  boss phase: ${imm.phase.name} is ${p.name} by its effect (${p.by})`); imm.phase = p; }
        else if (p.by !== 'type') imm.phase.by = p.by;
      }
    } else if (imm.phase && zb && imm.phaseId === zb.id) {
      const el = (nowI - imm.since) / 1000;
      // Iteration 42: Primus's phases counted (0 Force, 1 Adapt, 2 Rage) by its changes.
      if (imm.phase.name === "Primus's phase change" && PRIMUS.test(zb.type || '')) { if (primusSt.id !== zb.id) { primusSt.id = zb.id; primusSt.stage = 0; } primusSt.stage++; }
      log(`  boss phase over: ${imm.phase.name} after ${el.toFixed(1)}s${imm.phase.len ? ` (expected ${imm.phase.len})` : ''}${zb ? ` - ${zb.name} at ${Math.round(zb.hp)}, ${dist(me, zb.position).toFixed(1)}m off` : ''} | hp ${Math.round(hero.hp)}`);
      imm.phase = null;
    }
    const ph = imm.phase && zbBlock && !shortHold(zbBlock) ? phaseHold(imm.phase, imm.since, nowI) : null;
    // The frozen fallback keeps the fight's range (the hold is short); a known end brings the hero into reach before it.
    // Iteration 39: Primus kept at the edge of the basic attack's reach, its melee weighed wider (primusOpts).
    // Iteration 51: Infernus kept past its Atk's wedge (infernusOpts).
    const planOpts = infernusOpts(primusOpts(ct.why === 'shielded' ? { keep: { pos: ct.shielded.position, r: BOSS_KEEP } } :
      ct.why === 'invulnerable' && shortHold(firstBlock) ? {} :
      ct.why === 'invulnerable' && ph && ph.pre ? { desired: PRE_RANGE, keep: { pos: first.position, r: PRE_KEEP } } :
      ct.why === 'invulnerable' ? { desired: INVUL_RANGE, keep: { pos: first.position, r: BOSS_KEEP } } : {}, target, aRange,
      PRIMUS.test(target.type || '') ? primusStage(threats, primusSt.id === target.id ? primusSt : null) : 0), target, aRange);
    if (PRIMUS.test(target.type || '') && primusSaid !== target.id) {
      primusSaid = target.id;
      log(`  Primus: ${primusReaders(threats) ? `the mod lists its blows' shapes (readers ${threats.readers}) - its drawings (blobs) left out, the last dash kept for its red` : `no reader of its blows' shapes (readers ${threats.readers ?? 'none'}) - a blind boss: the last dash is not kept`}; held at ${planOpts.desired || '?'} m, its melee weighed to ${PRIMUS_MELEE} m | hp ${Math.round(hero.hp)}/${Math.round(hero.maxHp || 0)}`);
    }
    if (INFERNUS.test(target.type || '') && planOpts.bossMelee === INFERNUS_MELEE && infernusSaid !== target.id) { infernusSaid = target.id; log(`  Infernus: held at ${planOpts.desired} m, past its Atk's wedge (7.42 m from it + the body), its melee weighed to ${INFERNUS_MELEE} m | hp ${Math.round(hero.hp)}/${Math.round(hero.maxHp || 0)}`); }
    if (ph && ph.pre && ct.why === 'invulnerable' && !imm.preSaid) { imm.preSaid = true; log(`  boss phase: ${imm.phase.name} ends in ~${ph.left.toFixed(1)}s - closing to ${PRE_RANGE} m of ${first.name} (${d.toFixed(1)}m now), dash ${((hero.skills.find(k => k.slot === 'Movement') || {}).trigger || {}).charges ?? '?'} charges`); }
    if (ct.why !== shield.said || (ct.why === 'shielded' && target.id !== shield.targetId)) {
      const f = ct.shielded || first;
      const fb = blockOf(f) || 'invulnerable';
      if (ct.why === 'shielded') log(`  boss shielded: ${f.name} ${fb} at ${Math.round(f.hp)} - going for ${target.name} (${target.type}) ${Math.round(target.hp)}/${Math.round(target.maxHp || 0)} @${d.toFixed(1)}m, ${ct.must} of the kind that must die in sight | hp ${Math.round(hero.hp)}`);
      // Short holds (a Blink, the frozen fallback - which has its own line) are not announced, nor their end.
      else if (ct.why === 'invulnerable') { if (!shortHold(fb)) log(`  boss ${fb}: ${f.name} at ${Math.round(f.hp)}, nothing else to shoot - attacks and skills held, keeping ${INVUL_RANGE} m off | hp ${Math.round(hero.hp)}`); }
      else if (shield.said && !shield.short) log(`  boss shield: over after ${((Date.now() - shield.since) / 1000).toFixed(1)}s - ${first.name} at ${Math.round(first.hp)} | hp ${Math.round(hero.hp)}`);
      if (ct.why && !shield.said) shield.since = Date.now();
      shield.said = ct.why; shield.targetId = ct.why === 'shielded' ? target.id : null; shield.short = ct.why === 'invulnerable' && shortHold(fb);
    }
    const hpPct = hero.hp / hero.maxHp;
    const skill = s => hero.skills.find(k => k.slot === s);
    // run-018 Room_Sky_Combat_0_2: 309 -> 45 between two looks 60 ms apart (Big Baam's beam boxes near,
    // the hero in none of them) and no "took" line for it, ever. A drop of >= 15% of max hp is checked
    // against /damage 1.5 s later and logged when most of it never showed there.
    if (hpWatch.hp != null && hpWatch.hp - hero.hp >= 0.15 * hero.maxHp && !hpWatch.drop)
      hpWatch.drop = { amount: hpWatch.hp - hero.hp, from: hpWatch.hp, t: Date.now(), total: hpWatch.total, fx: (hero.statusEffects || []).map(e => e.type).filter(t => !/^Se_Star_|^Se_Hero/.test(t)).join(',') };
    if (hpWatch.drop && Date.now() - hpWatch.drop.t > 1500) {
      await readHits(); dmgAt = Date.now();
      const seen = hitTotal - hpWatch.drop.total, dr = hpWatch.drop;
      if (seen < 0.5 * dr.amount) log(`  hp ${Math.round(dr.from)} -> ${Math.round(dr.from - dr.amount)} in one look with only ${Math.round(seen)} of it in /damage - fx ${dr.fx}`);
      hpWatch.drop = null;
    }
    hpWatch.hp = hero.hp; hpWatch.total = hitTotal;
    const ready = s => { const k = skill(s); return k && k.type && k.trigger && k.trigger.canCast; };
    // How long each worn skill's cast holds the hero (learnLock, once per type; not waited for).
    for (const k of hero.skills) if (k.type && k.id && ['Q', 'W', 'E', 'R'].includes(k.slot) && !skillLocks.has(k.type)) learnLock(k.id, k.type);
    // Iteration 32: each worn skill's area (learnShape, once per type) and what the skills land (/hero/use; neither waited for).
    for (const k of hero.skills) if (k.type && k.id && ['Q', 'W', 'E', 'R'].includes(k.slot) && !shapeCM.has(k.type)) learnShape(k.id, k.type);
    readUse().catch(() => {});
    // Iteration 20: each worn skill's reach and cooldown, once per type (Precision Shot's 5 m range held it back for 20 runs).
    for (const k of hero.skills) if (k.type && k.trigger && ['Q', 'W', 'E', 'R'].includes(k.slot) && !skillSaid.has(k.type)) { skillSaid.add(k.type); log(`  skill ${k.slot} ${k.type} lvl ${k.level}: range ${k.trigger.range}, cooldown ${k.trigger.maxCooldown}s, aim ${k.trigger.aim}${k.trigger.maxCharges > 1 ? ', ' + k.trigger.maxCharges + ' charges' : ''}`); }
    // Where the hero was at the last looks and whether a walk was in force (walkSpeed).
    moveHist.push({ t: Date.now(), x: hero.position.x, z: hero.position.z, walking: !!(lastDir.x || lastDir.z) && Date.now() - dashAt > 700 });
    while (moveHist.length > 2 && Date.now() - moveHist[0].t > 800) moveHist.shift();
    const range = (hero.attack && hero.attack.range) || 8;
    const close = dist(me, entities[0].position);
    // Iteration 28: shots that blow up where they land (lobbedAreas) are strikes at their landing point, not shots to dodge.
    const lob = lobbedAreas(threats.projectiles || [], threats.areas || []);
    for (const s of (threats.projectiles || []).filter(s => lob.ids.has(s.id))) lobRadius(s);
    if (lob.areas.length && !lobSaid) { lobSaid = true; log(`  lobbed: ${lob.areas.length} ${lob.areas[0].type} coming down ${lob.areas.map(a => dist(me, a.centre).toFixed(1) + 'm').join(', ')} off (r ${lob.areas[0].radius}, landing in ${lob.areas[0].left}s) - kept off as strikes | hp ${Math.round(hero.hp)}`); }
    // Iteration 42: Primus's Adapt Atk bolts flying at a point are strikes where they land, with the chain's next links (adaptBolts).
    const primusE = bosses.find(e => PRIMUS.test(e.type || ''));
    // Iteration 45: Primus's Force phase paced (primusPace) - the skills held and the basic attack on a budget until its combat
    // clock (the Adaptation's) nears PACE_T, then the burn.
    let pace = null;
    if (primusE) {
      const pb = blockOf(primusE);
      const po = { id: primusE.id, hp: primusE.hp, maxHp: primusE.maxHp || 0, stage: primusStage(threats, primusSt.id === primusE.id ? primusSt : null),
        immune: !!pb && pb !== 'frozen', inReach: dist(me, primusE.position) <= range - 0.3, now: Date.now() };
      pace = primusPace(paceSt, po);
      if (pace.note) {
        const line = paceLine(pace, paceSt, po);
        if (line) log(`  ${line} | hp ${Math.round(hero.hp)}`);
        if (pace.note === 'burn' || pace.note === 'over') emit('primus_pace', { note: pace.note, why: pace.why, clock: Math.round(paceSt.clock * 10) / 10, hp: Math.round(primusE.hp),
          expected: adaptHp(paceSt.forceMax, pace.note === 'over' ? paceSt.clock : paceSt.clock + primusE.hp / PACE_BURN) });
      }
      if (po.stage === 1 && !pb && !paceSt.maxSaid && paceSt.released) {
        paceSt.maxSaid = true;
        log(`  primus pace: phase 2 came back with ${Math.round(primusE.maxHp || 0)} max hp (expected ${adaptHp(paceSt.forceMax, paceSt.clock)} for ${paceSt.clock.toFixed(1)} s of the bot's clock) | hp ${Math.round(hero.hp)}`);
        emit('primus_pace', { note: 'adapt', clock: Math.round(paceSt.clock * 10) / 10, maxHp: Math.round(primusE.maxHp || 0), expected: adaptHp(paceSt.forceMax, paceSt.clock) });
      }
    }
    const paceHold = !!(pace && pace.hold);
    const bolts = adaptBolts(threats.projectiles || [], primusE, threats.areas || []);
    const shots = (threats.projectiles || []).filter(s => !HARMLESS_SHOT.test(s.type || '') && !lob.ids.has(s.id) && !bolts.ids.has(s.id));
    // The Seeker's chaser orbs (chaserOrbs): their drawing is dropped, a small ring kept round each.
    const co = chaserOrbs(shots, threats.areas || []);
    if (co.orbs.length && !orbSaid) { orbSaid = true; log(`  chaser orb: ${co.dropped.length ? `its drawing (a blob of ${co.dropped.map(a => a.radius + 'm').join(', ')}) left out, ` : ''}keeping ${ORB_KEEP} m from it`); }
    // The burning pools (notePools) join the red: walking and dash landings keep off them.
    // Iteration 35: Primus's phase-change blast is no timed red - a ring kept out of near the change (blastKeep), or nothing.
    // Iteration 39: Primus's drawings dropped when the mod lists its blows' real shapes (primusDrawings).
    // Iteration 42: without the mod's readers 42 its Adapt Atk is read from its drawing (primusAdapt) - before the drawings go.
    const pa = primusAdapt(co.rest, primusE, threats, adaptSeen, Date.now());
    for (const c of pa.casts) log(`  Primus's Adapt Atk: its drawing (a ${c.drawn} m blob) ${dist(me, c.centre).toFixed(1)}m from the hero, ${dist(primusE.position, c.centre).toFixed(1)}m from Primus - a ${(c.drawn / ADAPT_DRAW).toFixed(1)} m strike in ~${ADAPT_T}s, its next links out from Primus | hp ${Math.round(hero.hp)}, dash ${((hero.skills.find(k => k.slot === 'Movement') || {}).trigger || {}).charges ?? '?'} charges`);
    const areas = readAreas(primusDrawings(pa.areas, threats, bosses).filter(a => a.shape !== 'zone' && a.shape !== 'safe' && a.shape !== 'blast' && !BH_HIT.test(a.type || '')).concat(lob.areas, bolts.areas), strikesSeen, Date.now()).concat(poolAreas(pools, Date.now()), co.orbs.map(orbArea), blastKeep(threats.areas), movingZones(threats.areas));
    // Iteration 42: Starfall's blows widened by how far they can still creep after the hero (starCreep).
    starCreep(areas);
    // Iteration 46: Doom's meteors (the mod's `doom`, readers 46, else their strikes remembered) and the fireballs in flight, each
    // on its meteor's spokes or not; the Arbalest's aim boxes (no dash before the last moment); the Adapt Atk's clock (Precision
    // Shot's cap).
    const doomM = primusE ? doomNote(doomSt, threats, threats.areas || [], Date.now()) : [];
    const doomS = doomM.length || shots.some(x => DOOM_SHOT.test(x.type || '')) ? doomShots(shots, doomM) : [];
    const arb = arbalestBoxes(areas);
    if (primusE) adaptNote(adaptSt, { now: Date.now(), primusAt: primusE.position, arbalest: [...arb].some(a => ARB_AIM.test(a.type || '')),
      casting: areas.some(a => a && isStrike(a) && /Adapt_Atk/.test(a.type || '') && !/next link/.test(a.type || '')) });
    for (const m of doomM) {
      const key = Math.round(m.c.x * 2) + ':' + Math.round(m.c.z * 2) + ':' + Math.round(m.landAt / 1000);
      const tl = (Date.now() - m.landAt) / 1000;
      if (tl < 1 || tl > 3 || doomSt.checked.has(key)) continue;
      doomSt.checked.add(key);
      const ck = doomCheck(m, shots);
      log(`  doom: a meteor at (${m.c.x.toFixed(1)}, ${m.c.z.toFixed(1)}) landed ${tl.toFixed(1)}s ago, ${dist(me, m.c).toFixed(1)}m off - ${ck.seen} of its fireballs listed, ${ck.on} on the model's spokes (${m.n} a ring, every ${m.step / 2} deg, ${m.v} m/s; ${doomSt.from}) | hp ${Math.round(hero.hp)}`);
    }
    // Iteration 35: the endgame's readers (proposals/iter-35-mod.md), each kind logged the first time it is listed.
    for (const a of threats.areas || []) {
      if (!a || !a.type || !ENDGAME_AREA.test(a.type) || endgameSaid.has(a.type)) continue;
      endgameSaid.add(a.type);
      const dp = a.centre ? areaDepth(me, a.shape === 'blast' || a.shape === 'safe' ? { ...a, shape: 'circle' } : a, 0) : 0;
      log(`  endgame: /threats lists ${a.type} - a ${a.shape}${a.radius ? ' of ' + a.radius + ' m' : ''}${a.left != null && a.left < 90 ? ', ' + a.left + 's left' : ''}${a.shape === 'blast' ? ', armed ' + a.fill : ''}, the hero ${a.centre ? dist(me, a.centre).toFixed(1) + 'm from its centre' : '?'}${dp > 0 ? ' (in it)' : ''} | hp ${Math.round(hero.hp)}`);
    }
    // Iteration 41 (run-049's death): a Displacer that can dash within its reach (dasherNear: no blind Precision Shot charge), and a
    // miniboss's spinning arrows (spinStream: the sweep of the next SPIN_AHEAD s kept out of, a dash through the stream as it
    // comes, circling the way it turns).
    const dasher = dasherNear(me, entities, threats.dashers);
    if (dasher && spinSt.dasherSaid !== dasher.name) { spinSt.dasherSaid = dasher.name; log(`  dasher: ${dasher.name} @${dasher.d.toFixed(1)}m can dash at the hero (its reach ~${dasher.reach.toFixed(1)} m${dasher.charges != null ? `, ${dasher.charges} charges` : ''}; ${dasher.from}) - no Precision Shot charged over ${P_MIN}s within it | hp ${Math.round(hero.hp)}`); }
    const spin = spinStream(shots, entities, threats.spinners);
    if (spin) {
      areas.push(spinArea(spin));
      if (spinSt.said !== spin.by) { spinSt.said = spin.by; log(`  spinning arrows: ${spin.by} - turning ${spin.turn > 0 ? 'ccw' : 'cw'} ~${Math.round(spin.omega)} deg/s, arrows ${Math.round(spin.speed * 10) / 10} m/s reaching ${spin.reach.toFixed(1)} m (r ${spin.radius}; ${spin.from}), the hero ${dist(me, spin.centre).toFixed(1)}m off - its sweep kept out of, circling ${spin.turn > 0 ? 'ccw' : 'cw'} with it, a dash through it as it comes | hp ${Math.round(hero.hp)}`); }
    }
    // Skoll's Death From Above: the drawing that follows the hero is its landing, a 4.5 m strike ~1.9 s off.
    const dfaNow = skollLanding(areas, bosses.find(e => /BossSkoll/.test(e.type || '')), dfa, Date.now());
    // Iteration 22: Infernus's Stomp - the eruption waves not listed yet, carried out along each ray (stompFan).
    const infernus = bosses.find(e => /BossInfernus/.test(e.type || ''));
    // Iteration 33: its breath (breathRead) - the hero's velocity over the last ~0.3 s for where the jets meet it.
    let br = null;
    if (infernus) {
      const h0 = moveHist.find(x => Date.now() - x.t < 400), h1 = moveHist[moveHist.length - 1];
      const vdt = h0 && h1 && h1 !== h0 ? (h1.t - h0.t) / 1000 : 0;
      br = breathRead(breath, shots, infernus, me, Date.now(), orbitSide, vdt > 0.1 ? { x: (h1.x - h0.x) / vdt, z: (h1.z - h0.z) / vdt } : null);
      if (br && br.jets && !breath.said) {
        breath.said = true;
        breathNumbers().catch(() => {});
        const j = shots.filter(x => BREATH.test(x.type || ''));
        log(`  Infernus's breath: ${j.length} jet${j.length > 1 ? 's' : ''} (speed ${j.map(x => x.speed).join('/')} m/s, radius ${j.map(x => x.radius).join('/')} m, reach ~${br.reach.toFixed(1)} m) - circling it at ${BREATH_R} m ${br.side > 0 ? 'ccw' : 'cw'}, away from its facing (${Math.round(br.gap)} deg off it), the hero ${br.d.toFixed(1)}m off, dash ${ready('Movement') ? 'ready' : 'not ready'} | hp ${Math.round(hero.hp)}`);
      }
    }
    if (infernus) {
      const fan = stompFan(areas, infernus.position);
      if (fan.length) {
        areas.push(...fan);
        if (Date.now() - fanSaidAt > 3000) { fanSaidAt = Date.now(); log(`  Infernus's Stomp: its eruptions carried out along their rays - ${fan.length} more predicted, the hero ${dist(me, infernus.position).toFixed(1)}m from it | hp ${Math.round(hero.hp)}`); }
      }
    }
    // Iteration 50: Azurak's Atk - its wind-up's strike moved to where the blow lands (without readers 50), its trail's next steps.
    const azurak = bosses.find(e => /BossAzurak/.test(e.type || ''));
    if (azurak || azSt.steps) {
      const az = azurakAtk(areas, azSt, { now: Date.now(), me, boss: azurak, readers: threats.readers });
      areas.push(...az.add);
      for (const nt of az.notes) {
        if (nt.kind === 'windup' && azSaid.windup++ < 3) log(`  Azurak's Atk: its wind-up drawn on him (${nt.drawn.toFixed(1)}m from him), the blow lands ~${Math.min(AZ_OFF, nt.d).toFixed(1)} m ahead toward where the hero stood (${nt.d.toFixed(1)}m off) - a strike there, the hero ${dist(me, nt.centre).toFixed(1)}m from it (readers ${threats.readers ?? 'none'}) | hp ${Math.round(hero.hp)}`);
        if (nt.kind === 'trail' && !azSaid.trail) { azSaid.trail = true; log(`  Azurak's Atk trail: ${nt.n} chain${nt.n > 1 ? 's' : ''} of steps (${AZ_GAP} m every ${AZ_STEP}s, turning <= ${AZ_TURN} deg toward the hero) - the next steps listed ahead | hp ${Math.round(hero.hp)}`); }
      }
    }
    // Iteration 54: the monsters' lanes (the Snow Wolf's Pounce, Big Baam's beam, the SwiftStep's shots) widened by the hero's body;
    // each watched kind said once a cast (readers 54 lists them from the wind-up) and kept for the line on a hit.
    laneReach(areas);
    watchReaders = threats.readers ?? null;
    watchNote(watchSeen, areas, me, Date.now());
    for (const l of watchSay(watchSaid, areas, me, Date.now())) log(`${l} | hp ${Math.round(hero.hp)}, dash ${((hero.skills.find(k => k.slot === 'Movement') || {}).trigger || {}).charges ?? '?'} charges`);
    addReach(areas);
    if (dfaNow && dfa.said !== dfa.first) {
      dfa.said = dfa.first;
      log(`  Skoll: Death From Above - its landing follows the hero (a ${dfaNow.drawn} m drawing, ${dist(me, dfaNow.centre).toFixed(1)} m off): a ${DFA_R} m strike in ${dfaNow.left}s | hp ${Math.round(hero.hp)}, dash ${ready('Movement') ? 'ready' : 'not ready'}`);
    }
    // Nyx's Blackhole (blackholeNow): its ring joins the red; the dash and the casts are below.
    const nyx = bosses.find(e => /BossNyx/.test(e.type || ''));
    let bhs = null;
    if (nyx) {
      if (Date.now() - bh.readAt > 250) { bh.readAt = Date.now(); await bossBlackhole(); }
      bhs = blackholeNow({ area: (threats.areas || []).find(a => BH_HIT.test(a.type || '')), polled: bh.polled, hit: bhHit, boss: nyx, centre: await roomCentre(),
        dmgR: bh.cfg && typeof bh.cfg.tickDamageRadius === 'number' ? bh.cfg.tickDamageRadius : BH_R, now: Date.now() });
      const said = bhs ? (bhs.on ? 'on' : bhs.soft ? 'maybe' : 'coming') : null;
      if (said !== bh.said) { bh.said = said; log(`  blackhole: ${bhs ? `${said} (${bhs.how}) at (${bhs.centre.x.toFixed(1)}, ${bhs.centre.z.toFixed(1)}), the hero ${dist(me, bhs.centre).toFixed(1)}m from it` : 'none'} - hp ${Math.round(hero.hp)}`); }
      if (bhs) areas.push(blackholeArea(bhs));
      // Iteration 28: her phase change's explosion (nyxPhaseRead), read while she cannot be hurt and until it has gone off.
      if ((zbBlock || nyxPhase.area) && !(threats.areas || []).some(a => a.type === PC_TYPE) && Date.now() - nyxPhase.readAt > 250) { nyxPhase.readAt = Date.now(); await nyxPhaseRead(nyx, await roomCentre()); }
      if (nyxPhase.area && !(threats.areas || []).some(a => a.type === PC_TYPE)) {
        areas.push({ ...nyxPhase.area });
        addReach(areas);
        if (nyxPhase.said !== nyxPhase.area.created) { nyxPhase.said = nyxPhase.area.created; log(`  Nyx's phase change: it explodes ${nyxPhase.area.radius} m round (${nyxPhase.area.centre.x.toFixed(1)}, ${nyxPhase.area.centre.z.toFixed(1)}) in ${nyxPhase.area.left}s - the hero ${dist(me, nyxPhase.area.centre).toFixed(1)}m from it | hp ${Math.round(hero.hp)}`); }
      }
    }
    // White Night's Cataclysm (cataclysmRead) and the Ink boss room's damaging ground (readGrounds).
    const whiteNight = bosses.find(e => /BossWhiteNight/.test(e.type || ''));
    const notSafe = (hero.statusEffects || []).some(e => e.type === NOT_SAFE);
    if (whiteNight || notSafe || /Ink_Boss/.test(room.room || '')) {
      if (Date.now() - cata.readAt > 250) { cata.readAt = Date.now(); await cataclysmRead(threats.areas); }
      await readGrounds();
      if (grounds.list.length) notePools(pools, { grounds: grounds.list, now: Date.now() });
      const said = cata.safe ? 'safe' : cata.on ? 'on' : notSafe ? 'blind' : null;
      if (said !== cata.said) {
        cata.said = said;
        log(`  cataclysm: ${said === 'safe' ? `safe circles known (${cata.safe.how})` : said === 'on' ? 'going, between waves' : said === 'blind' ? `the hero is NotSafe and no safe circle is known (${cata.cfg ? 'the SafeZone read failed' : 'no read'})` : 'over'} - hp ${Math.round(hero.hp)}`);
      }
      // Iteration 28: her Destruction Wave (waveSlice) - the slice still to be swept joins the red.
      // Iteration 49: its real shape, a 17.5 m spike (waveAreas): the spike now, the fan it sweeps, the spike at the turn's end -
      // from the mod (readers >= WAVE_READERS) or read here. Logged once a Wave.
      if (whiteNight) {
        const listed = (threats.areas || []).filter(x => x.type === WAVE_TYPE);
        if (!listed.length) {
          if (Date.now() - wave.readAt > 250) { wave.readAt = Date.now(); await waveRead(whiteNight); }
          for (const a of wave.areas) areas.push({ ...a });
        }
        const wa = listed.length ? listed : wave.areas;
        if (wa.length) {
          const fresh = Date.now() - (wave.seenAt || 0) > 1500;
          wave.seenAt = Date.now();
          if (fresh) log(`  ${waveSay(wa, me, whiteNight.position, listed.length ? 'the mod' : 'read')} | hp ${Math.round(hero.hp)}, dash ${((hero.skills.find(k => k.slot === 'Movement') || {}).trigger || {}).charges ?? '?'} charges`);
        }
      }
    }
    // Iteration 51: Dark Moon's Blade (her attack: a dash to 3.5 m short of the hero, then 0.85 s, then a crescent to 6.3 m) - its
    // polygon filled in and grown BLADE_REACH, from the mod when it lists it (grown here), else read here every look while she is in sight.
    const darkMoon = bosses.find(e => /^Mon_Ink_BossDarkMoon$/.test(e.type || ''));
    if (darkMoon) {
      for (let i = 0; i < areas.length; i++) if (areas[i] && areas[i].type === BLADE_TYPE && areas[i].shape === 'poly' && !areas[i].grown) areas[i] = bladeGrow(areas[i]);
      let ba = areas.filter(a => a && a.type === BLADE_TYPE && a.blade), from = 'the mod';
      if (!ba.length) {
        await bladeRead(darkMoon, me);
        if (blade.area) { areas.push({ ...blade.area }); ba = [blade.area]; from = `read, ${blade.area.how}`; }
      }
      if (ba.length) {
        const key = (blade.created ?? 'mod') + (ba[0].predicted ? ':p' : '');
        if (key !== blade.key || Date.now() - blade.seenAt > 1500) log(`  ${bladeSay(ba[0], me, from)} | hp ${Math.round(hero.hp)}, dash ${((hero.skills.find(k => k.slot === 'Movement') || {}).trigger || {}).charges ?? '?'} charges`);
        blade.key = key; blade.seenAt = Date.now();
      }
    }
    // Iteration 35: Azurak's Roar - into a pillar's shadow and stood in it, as the Cataclysm's circles (cataclysmStep).
    if (!whiteNight && !notSafe) {
      const rs = roarSafe(threats.areas);
      if (rs || cata.roar) {
        const was = !!cata.roar;
        cata.safe = rs; cata.roar = !!rs; cata.on = !!rs;
        if (!!rs !== was) log(`  Azurak's roar: ${rs ? `${rs.points.length} shadows of ${rs.radius.toFixed(1)}m (${rs.how}), the next roar in ${rs.left.toFixed(1)}s, the nearest ${Math.min(...rs.points.map(p => dist(me, p))).toFixed(1)}m off` : 'over'} - hp ${Math.round(hero.hp)}`);
        if (!rs) { cata.key = null; cata.spot = null; }
      }
    }
    const bhD = bhs ? dist(me, bhs.centre) : 99;
    const nPools = areas.filter(a => a.pool).length;

    if (Date.now() - lastLog > 3000) {
      lastLog = Date.now();
      await readHits(); dmgAt = Date.now();
      if (Object.keys(tookAcc).length) log('  took', tookLine());
      log(`hp ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} lvl ${hero.level} | ${entities.length} near, target ${target.name} ${Math.round(target.hp)}hp${target.invulnerable ? " (invulnerable)" : ""} @${d.toFixed(1)}m, ${shots.length} shots, ${areas.length - nPools} red areas${nPools ? ` + ${nPools} fire pools` : ''}${areas.some(x => areaDepth(me, x, 0) > 0) ? ' (standing in one)' : ''}${burning(hero) ? ', burning' : ''}, circling ${orbitSide > 0 ? 'ccw' : 'cw'}`);
    }

    // Nothing within reach: walk a real path to it (a direction cannot go around a chasm).
    if (close > 30 && !st.hero.inCombat && Date.now() - lastNearbyLoot > 5000) { await stopDir(); lootAbort = 25; lootDanger = true; try { await loot(16, 6, true); } finally { lootAbort = 0; lootDanger = false; } lastNearbyLoot = Date.now(); continue; }
    // Walking up is a straight line, which is what a boss that shoots at where the hero will be
    // (the Seeker's volleys) hits every time: both Seeker deaths (run 28, run-001) began at 12-14 m,
    // walking into its red circles. So with a red shape near or a shot on its way, walk up the
    // way a fight moves - around them, out of the red, dashing when need be - not straight in.
    // Iteration 28: and timed red on the first WALKUP_LOOK m of the straight way (redInWay) - the walk-up went into whatever lay
    // ahead of the hero's first 3 m (a Barrage's circles, Nyx's phase-change blast round the room's centre).
    const aheadRed = close > 12 ? redInWay(areas, me, alongTo(me, target.position, WALKUP_LOOK), 3) : null;
    const threatened = areas.some(a => !a.pool && areaDepth(me, a, 3) > 0) || (bhs && !bhs.soft) || !!cata.safe || !!aheadRed ||
      shots.some(s => !s.homing && s.eta < 1.5 && s.miss < s.radius + 1.5);
    if (aheadRed && walkRedSaid !== (aheadRed.area.type || aheadRed.area.shape) + target.id) { walkRedSaid = (aheadRed.area.type || aheadRed.area.shape) + target.id; log(`  ${target.name} @${d.toFixed(0)}m: a ${aheadRed.area.shape} of ${aheadRed.area.by || aheadRed.area.type || '?'} (${aheadRed.area.radius} m, lands in ${aheadRed.area.left}s) on the straight way - closing in round it`); }
    // A pillar may stand on the lava (they are immune to it; the hero is not): the straight walk-up goes
    // only where /nav/path finds no lava on the way, else plan() closes in over the grid's safe cells.
    let straightOk = true;
    // Iteration 14: any far target in LavaLand, not only a pillar (the walk-up is /hero/move's navmesh path).
    if (close > 12 && !threatened && onLavaZone()) {
      // Iteration 33: looked at again every 3 s (it moves - round the lava to this side, or away).
      if (!shield.lavaWay.has(target.id) || Date.now() - (shield.lavaAt.get(target.id) || 0) > 3000) {
        const p = await get('/nav/path', { x: target.position.x, z: target.position.z }).catch(() => null);
        const was = shield.lavaWay.get(target.id);
        shield.lavaWay.set(target.id, !p || !(p.onHazard > 0.5)); shield.lavaAt.set(target.id, Date.now());
        if (p && p.onHazard > 0.5 && was !== false) log(`  ${target.name} @${d.toFixed(0)}m: ${p.onHazard.toFixed(1)}m of lava on the way - closing in round it`);
      }
      straightOk = shield.lavaWay.get(target.id);
    }
    // Iteration 21: nor through a burning pool or the Ink boss room's damaging ground (poolOnWay) - run-030's Dark Moon:
    // she stood in the ground (r 10.5, 4% of max hp every 0.5 s) and both straight walk-ups (from 33.5 m, then from 16.9 m
    // during her shield) went through it: 114 + 38 + 114. plan() keeps off pool cells; it closes in round them.
    const pw = close > 12 && !threatened && straightOk ? poolOnWay(areas, me, target.position) : null;
    if (pw && poolSaid !== target.id) { poolSaid = target.id; log(`  ${target.name} @${d.toFixed(0)}m: ${pw.fixed ? pw.src || 'a fixed fire' : 'a burning pool'} (${pw.radius} m) on the straight way - closing in round it`); }
    // Iteration 22: nor past a shielded boss - run-033 walked straight up to a pillar 39 m off across the arena, Infernus in
    // between (18 -> 9.2 m from it), then dashed away from it into its Stomp: 119 and the throw onto the lava.
    const pastBoss = ct.why === 'shielded' && ct.shielded && ct.shielded.position && segDist(ct.shielded.position, me, target.position) < WALKUP_KEEP;
    if (pastBoss && close > 12 && !threatened && straightOk && !pw && shield.pastSaid !== target.id) { shield.pastSaid = target.id; log(`  ${target.name} @${d.toFixed(0)}m: ${ct.shielded.name} ${segDist(ct.shielded.position, me, target.position).toFixed(1)}m off the straight way - closing in round it`); }
    if (close > 12 && !threatened && straightOk && !pw && !pastBoss) {
      if (Date.now() - lastPathAt > 1000) { await stopDir(); await tryPost('/hero/move', { x: target.position.x, z: target.position.z }); lastPathAt = Date.now(); }
      if (bosses.length) trace(new Date().toISOString().slice(11, 23) + ' walk up: boss d ' + dist(me, bosses[0].position).toFixed(1) + ' hp ' + Math.round(hero.hp) + ' shots ' + shots.length + ' areas ' + areas.length);
      await sleep(150);
      continue;
    }
    if (close > 12) lastPathAt = 0;   // back to walking a path the moment the danger is past

    // Infernus's meteor rain (iteration 17; run-023's death): each meteor (a 1.5 m strike, ~0.7 s to land) falls
    // where the hero is when it is thrown, so standing or turning back is what gets hit - while they come, plan()
    // favours going on the way the hero is walking (momentum); crossRed keeps walks out of those about to land.
    // Iteration 18: Nyx's Starfall too (RAIN) - run-026's walk turned each look between two strikes and was hit.
    if (areas.some(a => RAIN.test(a.type || ''))) meteorAt = Date.now();
    if (Date.now() - meteorAt < 2500 && (lastDir.x || lastDir.z)) planOpts.momentum = lastDir;
    // A pillar out of reach (iteration 17): a place to shoot it from (shootSpot) - walked to on dry ground (plan()'s
    // approach, the way read off a grid big enough to hold it), or dashed to across the lava from the cell short of it.
    let spotNow = null;
    // Iteration 33: and any LavaLand target out of reach with lava on the way (shield.lavaWay) - a dry place to shoot it
    // from, walked to round the lava, or a dash over it; none: the edge held (lavaHeld, below), not paced.
    const lavaTgt = onLavaZone() && !ct.why && !bosses.length && shield.lavaWay.get(target.id) === false;
    if (((ct.why === 'shielded' && MUST_KILL.test(target.type || '')) || lavaTgt) && d > range - 0.3) {
      if (spot.id !== target.id || Date.now() - spot.at > 1000) {
        spot.id = target.id; spot.at = Date.now();
        spot.g = await get('/nav/grid', { radius: Math.min(40, Math.ceil(d + range)), step: 1 }).catch(() => null);
        const keep = ct.shielded ? { pos: ct.shielded.position, r: BOSS_KEEP - 1 } : null;
        spot.s = spot.g ? shootSpot(spot.g, target.position, range - 0.8, keep) : null;
        // Iteration 21: none in the attack's reach - one in Precision Shot's far reach (P_CAP_FAR), if the hero has it.
        const farR = precisionWorn(hero) ? precisionReach(precision.cfg, P_CAP_FAR) - P_FAR_KEEP : 0;
        // Also when the attack's place is a long way round (10 m more to walk than the far shot's).
        if (spot.g && farR > range && (!spot.s || spot.s.cost > 10)) {
          const fsp = shootSpot(spot.g, target.position, farR, keep);
          if (fsp && (!spot.s || fsp.cost + 10 < spot.s.cost)) { spot.s = fsp; spot.s.far = farR; }
        }
        spot.path = spot.s ? safePath(spot.g, spot.s.walkTo, 1) : null;
        const key = target.id + ':' + (spot.s ? (spot.s.hop ? 'hop' : 'walk') + (spot.s.far ? ':far' : '') : 'none');
        if (spot.said !== key) {
          spot.said = key;
          log(`  ${target.name} @${d.toFixed(1)}m, out of reach (${range} m): ${spot.s ? `a place to shoot it from${spot.s.far ? ` with Precision Shot (its reach ${(spot.s.far + P_FAR_KEEP).toFixed(1)} m charged ${P_CAP_FAR} s)` : ''} at (${spot.s.p.x.toFixed(1)}, ${spot.s.p.z.toFixed(1)}), ${dist(spot.s.p, target.position).toFixed(1)}m from it, ${spot.s.hop ? `a dash across the lava from (${spot.s.walkTo.x.toFixed(1)}, ${spot.s.walkTo.z.toFixed(1)})` : 'on foot'}, ~${spot.s.cost.toFixed(0)}m` : `no dry place within ${(range - 0.8).toFixed(1)} m of it${farR > range ? ` (nor ${farR.toFixed(1)} m for Precision Shot)` : ''}, on foot or by a dash`}`);
        }
      }
      spotNow = spot.s;
    }
    if (spotNow) {
      const mvK = skill('Movement');
      const ch = mvK && mvK.trigger && mvK.trigger.charges != null ? mvK.trigger.charges : 0;
      if (spotNow.hop && dist(me, spotNow.walkTo) <= 1.2 && ready('Movement') && Date.now() - spot.hopAt > 1500 &&
        (ch >= 2 || !areas.some(a => !a.pool && areaDepth(me, a, 2) > 0))) {
        spot.hopAt = Date.now(); spot.at = 0;
        log(`  dash: across the lava to shoot ${target.name} (${dist(spotNow.p, target.position).toFixed(1)}m from it) | hp ${Math.round(hero.hp)}`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: spotNow.p.x, z: spotNow.p.z, move: false });
        if (bosses.length || hpPct < 0.4) trace('  dash to the shooting place ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0; gridAt = 0;
        continue;
      }
      let ap = spotNow.walkTo;
      if (spot.path && spot.path.length && dist(me, spotNow.walkTo) > 5) {
        let i = 0;
        for (let j = 1; j < spot.path.length; j++) if (dist(me, spot.path[j]) < dist(me, spot.path[i])) i = j;
        ap = dist(me, spot.path[i]) < 1.5 && i + 1 < spot.path.length ? spot.path[i + 1] : spot.path[i];
      }
      planOpts.approach = ap;
    }
    // Iteration 33: across the lava, out of reach, no place to shoot it from: stand at the edge and let it come (or come in
    // reach of the far shot) - for LAVA_HOLD_MAX at most per target, then as before.
    if (lavaTgt && d > range - 0.3 && !spotNow && spot.id === target.id && spot.g) {
      if (lavaHold.id !== target.id) { lavaHold.id = target.id; lavaHold.since = Date.now(); }
    } else if (lavaHold.id === target.id && (!lavaTgt || d <= range - 0.3 || spotNow)) lavaHold.id = null;
    const lavaHeld = lavaHold.id === target.id && Date.now() - lavaHold.since < LAVA_HOLD_MAX;
    if (lavaHeld && lavaHold.said !== target.id) { lavaHold.said = target.id; log(`  ${target.name} @${d.toFixed(1)}m across the lava, no dry place within reach of it, no dash over - holding the edge (up to ${LAVA_HOLD_MAX / 1000}s), letting it come | hp ${Math.round(hero.hp)}`); }

    if (!gridCache || Date.now() - gridAt > 300) { gridCache = await get('/nav/grid', { radius: 8, step: 1 }); gridAt = Date.now(); }
    // Iteration 33: while Infernus breathes, round it close and away from its facing (no flip; its swing is blocked).
    if (br && target === infernus) { planOpts.desired = BREATH_R; planOpts.bossMelee = BREATH_MELEE; planOpts.side = br.side; delete planOpts.momentum; }
    // Iteration 41: within the spinning arrows' reach, circling the way they turn - the stream comes round less often.
    if (spin && !planOpts.side && dist(me, spin.centre) <= spin.reach + 2) planOpts.side = spin.turn;
    // Iteration 44: pinned against a wall with them round the hero (pinRead / pinStep) - a break-out to the most open ground
    // within ~15 m (openArea on a wider grid, breakPlan): through the gap, the weak add in the way first, a dash along the way
    // (below, with the dashes), or the least crowded way. Not in Nyx's Blackhole, Infernus's breath, a Cataclysm or Azurak's
    // roar (their own moves), nor while holding a lava edge. Near a wall the orbit leans to the open side (plan()'s openDir).
    const nowP = Date.now();
    const pr = pinRead(gridCache, me, entities, { bossReach: planOpts.bossMelee || 5.5 });
    const pinOk = !(bhs && !bhs.soft) && !br && !cata.safe && !lavaHeld;
    const pinBy = pinStep(pin, pr, nowP);
    const pinSay = () => `room ${pr.room ?? '?'} m, ${pr.foes.length} of them within ${PIN_NEAR} m, ${pr.run}/${PIN_DIRS} ways free`;
    if (pin.on) {
      const over = !pinOk ? 'another move first' : breakOver(pin.on, pr, me, nowP);
      if (over) {
        const secs = (nowP - pin.on.from) / 1000, took = Math.round(hitTotal - pin.on.hit0);
        pin.n++; pin.secs += secs; pin.took += took;
        log(`  pinned: over after ${secs.toFixed(1)}s (${over}; ${pin.on.hows.join(' > ')}) - took ${took} while pinned, ${pinSay()} | hp ${Math.round(hero.hp)}`);
        pin.on = null; pin.since = 0; pin.stalls = []; pin.coolUntil = nowP + BREAK_COOL;
      }
    }
    if (pinOk && (pin.on ? pin.replan || nowP - pin.on.at > 700 : pinBy && nowP >= pin.coolUntil)) {
      if (!pin.big || nowP - pin.bigAt > 500) { pin.big = await get('/nav/grid', { radius: 14, step: 1 }).catch(() => null); pin.bigAt = Date.now(); }
      const mvP = skill('Movement'), chP = mvP && mvP.trigger && mvP.trigger.charges != null ? mvP.trigger.charges : undefined;
      // Timed red about to land near the hero (a telegraph filling, a strike, a roll's box): the last charge is kept for it.
      const redSoon = areas.some(a => !a.pool && !a.keepOut && !a.timeless && a.left > 0.2 && a.left < 2.5 && areaDepth(me, a, 3) > 0);
      const killNow = e => ['Q', 'W', 'E'].some(s => { const k = skill(s); if (!ready(s) || !k || COSTS_HEALTH.has(k.type)) return false; const dm = skillDmg(k, hero, 0); return dm != null && dm >= effHp(e) && dist(me, e.position) <= (k.trigger.range || 9); });
      const killSoon = e => effHp(e) <= 2 * ((hero.stats && hero.stats.attackDamage) || 0) && dist(me, e.position) <= range;
      const oa = openArea(pin.big, me, entities, areas, { bossReach: planOpts.bossMelee || 5.5, keep: planOpts.keep, weak: e => killNow(e) || killSoon(e) });
      const bo = breakPlan(oa, { dashReady: ready('Movement') && !(pin.on && pin.on.dashed), charges: chP, redSoon, killNow, killSoon });
      if (!bo) {
        if (!pin.on && nowP - pin.noneSaidAt > 5000) { pin.noneSaidAt = nowP; log(`  pinned: ${pinSay()} - no open ground within ${OPEN_PATH} m of walking; circling as before | hp ${Math.round(hero.hp)}`); }
        if (!pin.on) pin.coolUntil = nowP + BREAK_COOL;
      } else if (!pin.on) {
        pin.on = { from: pinBy === 'stall' ? Math.min(nowP, ...pin.stalls) : pin.since || nowP, since: nowP, at: nowP, by: pinBy, hit0: hitTotal, oa, ...bo, hows: [bo.how], dashed: 0 };
        log(`  pinned: ${pinSay()}${pinBy === 'stall' ? ', not moving twice' : `, cramped ${((nowP - pin.since) / 1000).toFixed(1)}s`} - open area at (${oa.p.x.toFixed(1)}, ${oa.p.z.toFixed(1)}) ${Math.round(oa.area)} m2, room ${oa.room} m, ${oa.len} m of walking - breaking out ${bo.why} | hp ${Math.round(hero.hp)}, dash ${chP ?? '?'} charges`);
      } else {
        const was = pin.on.how;
        Object.assign(pin.on, { oa, how: bo.how, to: bo.to, target: bo.target, why: bo.why, at: nowP });
        if (bo.how !== was) { pin.on.hows.push(bo.how); log(`  pinned: now ${bo.why} (open area at (${oa.p.x.toFixed(1)}, ${oa.p.z.toFixed(1)}) ${Math.round(oa.area)} m2, ${pinSay()}) | hp ${Math.round(hero.hp)}`); }
      }
      pin.replan = false;
    }
    if (pin.on) {
      if (pin.on.how !== 'dash') { const w = breakWay(pin.on.oa.path, me); if (w) { planOpts.approach = w; delete planOpts.momentum; } }
      if (pin.on.how === 'kill') {
        const t = pin.on.target && entities.find(e => e.id === pin.on.target.id && e.alive !== false);
        if (t && !cannot(t)) target = t; else { pin.on.how = 'push'; pin.on.hows.push('push'); }
      }
      if ((bosses.length || hpPct < 0.4)) trace(`  pin ${pin.on.how} -> ${(planOpts.approach || pin.on.to).x.toFixed(1)},${(planOpts.approach || pin.on.to).z.toFixed(1)} open ${pin.on.oa.p.x.toFixed(1)},${pin.on.oa.p.z.toFixed(1)} ${Math.round(pin.on.oa.area)}m2 crossed ${pin.on.oa.crossed.length} gap ${pin.on.oa.gap} | room ${pr.room} foes ${pr.foes.length} run ${pr.run}`);
    } else if (pr.openDir && pr.room != null && pr.room <= 3) planOpts.openDir = pr.openDir;
    // Iteration 46: Doom's fireballs (run-052: ~300 each, dead in 11 s) - the hero waits between their spokes, far from the
    // meteors (doomSpot), walks there and stands; plan()'s cells (the walk and a dash's landing) weigh their paths (doomRisk).
    if (doomM.length || doomS.length) {
      const nowD = Date.now();
      const ds = doomSpot(gridCache, me, doomM, doomS, areas, nowD, { keep: planOpts.keep, prev: doomSt.spot });
      if (ds) {
        const moved = !doomSt.spot || dist(doomSt.spot.p, ds.p) > 1.5;
        doomSt.spot = ds;
        planOpts.approach = ds.p; delete planOpts.momentum; delete planOpts.side;
        planOpts.doom = q => doomRisk(q, doomM, doomS, nowD);
        if (moved && nowD - doomSt.saidAt > 1000) {
          doomSt.saidAt = nowD; doomSt.n++;
          const landed = doomM.filter(m => m.landAt <= nowD).length;
          log(`  doom: ${doomM.length} meteor${doomM.length === 1 ? '' : 's'} (${landed} landed), ${doomS.length} fireballs - waiting at (${ds.p.x.toFixed(1)}, ${ds.p.z.toFixed(1)}), ${ds.md.toFixed(1)}m off, between their spokes: risk ${ds.risk.toFixed(1)} there, ${ds.here.toFixed(1)} here | hp ${Math.round(hero.hp)}, dash ${((skill('Movement') || {}).trigger || {}).charges ?? '?'}`);
        }
        if (bosses.length > 0 || hpPct < 0.4) trace(`  doom spot ${ds.p.x.toFixed(1)},${ds.p.z.toFixed(1)} ${ds.md.toFixed(1)}m risk ${ds.risk.toFixed(1)} here ${ds.here.toFixed(1)} cost ${ds.cost.toFixed(1)}${ds.kept ? ' kept' : ''} meteors ${doomM.map(m => `${m.c.x.toFixed(1)},${m.c.z.toFixed(1)}@${((m.landAt - nowD) / 1000).toFixed(1)}`).join(' ')} fireballs ${doomS.length}`);
      }
    } else doomSt.spot = null;
    const pl = plan(gridCache, me, entities, target, range, shots, areas, planOpts);
    // Iteration 51: the burning shots' approach, look by look, for the line on a hit (fireShotSay).
    noteFire(fireSeen, shots, me, !!pl.dodge, Date.now());
    // Standing in a red shape: how far to its edge, against how long until it lands (~5 m/s on foot).
    let areaEscape = null;
    for (const a of areas) {
      // Iteration 46: the Arbalest's aim follows the hero - its line is left by a dash in its last ARB_LATE s only (arbalestEarly).
      if (arbalestEarly(a, arb)) continue;
      const dp = areaDepth(me, a, 0.4);
      if (dp > 0 && (!areaEscape || a.left < areaEscape.left)) areaEscape = { depth: dp, left: a.left, shape: a.shape, by: a.by, timeless: a.timeless };
    }
    // Iteration 19: at the speed the hero really walks (walkSpeed: rooted, slowed by the cold, blocked), not 5 m/s.
    // Not in Nyx's Blackhole: its pull slows every walk outward, and its own dodge (bhDodge, iterations 15-18) is tuned to the old rule.
    const walkV = bhs && !bhs.soft ? null : walkSpeed(moveHist, Date.now());
    const areaUrgent = escapeUrgent(areaEscape, walkV);
    if (areaEscape) { areaEscape.v = walkV; areaEscape.slowUrgent = areaUrgent && !escapeUrgent(areaEscape, null); }

    // Walking and getting nowhere (a lip the grid missed, a body in the way): go round the other way.
    if (lastDir.x || lastDir.z) {
      if (!stuckFrom) { stuckFrom = me; stuckAt = Date.now(); }
      else if (dist(me, stuckFrom) > 0.8) { stuckFrom = me; stuckAt = Date.now(); }
      // Iteration 44: counted for pinStep ("not moving" twice in 4 s with them near is pinned); breaking out, the way is planned
      // again (a dash may be ready now) instead of a flip that walks the hero along the wall.
      else if (Date.now() - stuckAt > 900) {
        stuckFrom = null; pin.stalls.push(Date.now());
        if (pin.on) pin.replan = true;
        else { orbitSide = -orbitSide; orbitFlippedAt = Date.now(); log('  not moving - circling the other way'); }
      }
    } else stuckFrom = null;

    // Traced per tick: boss fights, and any fight with the hero under 40% - how the low-health
    // rooms go (run-002 died in one: Starfall circles and Night Olm ticks, nothing traced).
    // Each area: shape, radius, fill/left, caster, how far its centre is ("c4.2m"), how deep the
    // hero stands in it ("in1.3") and for an untimed blob its age ("age0.6s") - with the hero's
    // position and the hits, enough to tell a blob's real size and fuse from the read one.
    const traced = bosses.length > 0 || hpPct < 0.4;
    const nowMs = Date.now();
    for (const a of areas) if (a.timeless) {
      const k = Math.round(a.centre.x) + ':' + Math.round(a.centre.z) + ':' + Math.round(a.radius);
      const b = blobSeen.get(k);
      if (b) b.last = nowMs; else blobSeen.set(k, { first: nowMs, last: nowMs });
      a.age = (nowMs - blobSeen.get(k).first) / 1000;
    }
    for (const [k, b] of blobSeen) if (nowMs - b.last > 2000) blobSeen.delete(k);
    const mv = skill('Movement');
    const charges = mv && mv.trigger && mv.trigger.charges != null ? mv.trigger.charges : undefined;
    if (traced) trace(new Date().toISOString().slice(11, 23) + ' hp ' + Math.round(hero.hp) + ' at ' + me.x.toFixed(1) + ',' + me.z.toFixed(1) + (bosses.length ? ' boss ' + Math.round(bosses[0].hp) + ' d ' + dist(me, bosses[0].position).toFixed(1) : ' low: ' + entities.length + ' near, nearest ' + close.toFixed(1) + 'm ' + entities[0].name) + (bosses.length && bosses[0].invulnerable ? ' inv' : '') + (zbBlock && zbBlock !== 'invulnerable' ? ' imm:' + zbBlock : '') + (cannot(target) ? ' hold' : '') + (ph ? ' phase ' + ph.el.toFixed(1) + (ph.left != null ? '/' + ph.left.toFixed(1) + 'left' : '') + (ph.pre ? ' pre' : '') : '') + (zb && zb.shield > 0 ? ' shield ' + Math.round(zb.shield) : '') + (ct.why === 'shielded' ? ' tgt ' + target.type + ' ' + Math.round(target.hp) + ' d ' + d.toFixed(1) : '') + ' side ' + orbitSide + ' shots ' + shots.length + (shots.some(s => !s.homing) ? '(' + [...new Set(shots.filter(s => !s.homing).map(s => (s.type || '?').replace(/^Ai_(Mon_)?/, '')))].join('|') + ')' : '') + ' homing ' + shots.filter(s => s.homing).map(s => `${s.type}@${dist(me, s.position).toFixed(1)}m/${s.speed}mps/eta${s.eta}/r${s.radius}`).join(',') + ' areas ' + areas.map(a => { const dp = areaDepth(me, a, 0); return a.shape + (a.shape === 'box' ? '' : a.reach ? Math.round((a.radius - a.reach) * 100) / 100 + '+' + a.reach : a.radius) + (a.type ? '(' + a.type.replace(/^Ai_(Mon_)?/, '') + ')' : '') + '@' + a.fill + '/' + a.left + 's/' + (a.by || '?') + '/c' + dist(me, a.centre).toFixed(1) + 'm' + (a.timeless ? '/age' + a.age.toFixed(1) + 's' : '') + (dp > 0 ? '(in' + dp.toFixed(1) + ')' : ''); }).join(',') + ' dash ' + (ready('Movement') ? 'ready' : 'cd') + '/' + (charges ?? '?') + ' fx ' + (hero.statusEffects || []).map(e => e.type).filter(t => !/^Se_Star_|^Se_Hero/.test(t)).join(',') + (bosses.length ? ' bossAt ' + bosses[0].position.x.toFixed(1) + ',' + bosses[0].position.z.toFixed(1) : '') + (cata.on || cata.safe ? ' cata ' + (cata.safe ? cata.safe.points.map(p => p.x.toFixed(1) + ',' + p.z.toFixed(1)).join(';') + ' r' + cata.safe.radius.toFixed(1) + ' left' + cata.safe.left.toFixed(2) : 'on') : ''));
    // Iteration 33: the breath's jets as /threats gives them (speed, radius, what is left of the flight, eta, miss) - their numbers are prefab values.
    if (traced && br) trace(`  breath ${br.el.toFixed(2)}s gap ${Math.round(br.gap)} side ${br.side} d ${br.d.toFixed(1)}${br.hit ? ' HIT eta' + br.hit.eta : ''} jets ` + shots.filter(x => BREATH.test(x.type || '')).map(x => `${dist(me, x.position).toFixed(1)}m/${x.speed}mps/r${x.radius}/rem${x.remaining}/eta${x.eta}/miss${x.miss}/h${Math.round(angDeg(x.heading))}`).join(','));

    // On the lava (iteration 14). run-019 died in a LavaLand combat room: lava ticks 19 -> 22 (they grow
    // while the hero stays: LavaLand_Lava's currentStack) with the burn they relight, ~300 in 2-3 s, the
    // hero walking its circles on the lava with a dash charge ready all along. The grid's cells are off the
    // lava, but nothing here looked at the ground under the hero, and a charge (Bone Crusher) or a lunge
    // can carry it onto the lava. So: the hero's own grid cell is hazard, or a lava hit was read in the
    // last 0.9 s -> straight to the nearest cell with room (lavaExit), by dash when one is ready (a
    // displacement: the lava does not tick on it; any charge - the ticks grow and burn), else on foot,
    // with no casts or attacks until off.
    // Iteration 15: /hero's onHazard (the game's own IsEntityOnLava, the iter-14 mod) first - true is on it
    // now; false means a lava hit read in the last 0.9 s is behind us (hits are read up to 0.4 s late), so
    // only the grid's centre cell still counts then.
    const heroOnLava = hero.onHazard === true;
    if (onLavaZone() && (heroOnLava || gridOnHazard(gridCache) || (hero.onHazard !== false && nowMs - lavaHit.at < 900))) {
      let ex = lavaExit(gridCache, me, entities, areas);
      if (!ex || ex.d > 6) {
        if (!lava.big || nowMs - lava.bigAt > 600) { lava.bigAt = nowMs; lava.big = await get('/nav/grid', { radius: 16, step: 1 }).catch(() => null); }
        const e2 = lava.big && lavaExit(lava.big, me, entities, areas);
        if (e2 && (!ex || e2.d < ex.d)) ex = e2;
      }
      // The stay's lava damage counts the hits that showed it (run-021 logged "0 from it" after 35 + 12):
      // those read in the last second are taken back out of the starting total.
      if (!lava.since) { lava.since = nowMs; lava.from = hero.hp; lava.said = false; lava.total = lavaHit.total - lavaHit.recent.filter(r => nowMs - r.t < 1000).reduce((s, r) => s + r.amount, 0); }
      lava.last = nowMs;
      if (ex && ex.d > 0.7) {
        const onIt = heroOnLava || gridOnHazard(gridCache);
        const canDash = ready('Movement') && nowMs - lava.dashAt > 600 && (onIt ? ex.d > 1.5 : ex.d > 2.5);
        if (!lava.said || canDash) log(`  lava: under the hero in the fight (${heroOnLava ? 'on it, the game says' : gridOnHazard(gridCache) ? 'its grid cell is lava' : 'a lava hit'}; ${Math.round(lavaHit.total - lava.total)} from it so far, hp ${Math.round(hero.hp)}) - ${canDash ? 'dash' : 'walk'} to (${ex.p.x.toFixed(1)}, ${ex.p.z.toFixed(1)}), ${ex.d.toFixed(1)}m, ${ex.clear}m of room`);
        lava.said = true;
        if (canDash) {
          lava.dashAt = nowMs;
          // Aimed no farther than the fight's dashes go (5.5 m); a far exit is dashed toward, then walked.
          const s = Math.min(1, 5.5 / ex.d), at = { x: me.x + (ex.p.x - me.x) * s, z: me.z + (ex.p.z - me.z) * s };
          const r = await tryPost('/hero/cast', { slot: 'Movement', x: at.x, z: at.z, move: false });
          if (traced) trace('  dash off the lava ' + JSON.stringify(r).slice(0, 160));
          lastDirAt = 0; gridAt = 0;
          continue;
        }
        const dir = { x: (ex.p.x - me.x) / ex.d, z: (ex.p.z - me.z) / ex.d };
        if (Math.hypot(dir.x - lastDir.x, dir.z - lastDir.z) > 0.2 || Date.now() - lastDirAt > 300) { await tryPost('/hero/move_dir', dir); lastDir = dir; lastDirAt = Date.now(); }
        if (traced) trace(`  walk off the lava -> ${ex.p.x.toFixed(1)},${ex.p.z.toFixed(1)} ${ex.d.toFixed(1)}m`);
        await sleep(60);
        continue;
      }
      if (!ex && !lava.said) { lava.said = true; log(`  lava: under the hero in the fight and no cell off it within 16 m - hp ${Math.round(hero.hp)}`); }
    } else if (lava.since && nowMs - lava.last > 1500) {
      log(`  lava: off it after ${((lava.last - lava.since) / 1000).toFixed(1)}s - ${Math.round(lavaHit.total - lava.total)} from it, hp ${Math.round(lava.from)} -> ${Math.round(hero.hp)}`);
      lava.since = 0;
    }

    // Dash when walking will not do: red about to land, red a step cannot leave, a shot about to
    // hit (hard reasons); one of them on top of the hero, cornered, the boss close or charging, low
    // on health up close (soft - only with a charge left over, iteration 30: in every fight; see dashChoice).
    // Iteration 30: within melee reach (SOFT_NEAR 3 m, was 4.5) - see dashChoice.
    const cornered = pl.here && pl.here.clear <= 2 && close < SOFT_NEAR;
    let bossRushing = false;
    if (bosses.length) {
      const bd = dist(me, bosses[0].position);
      bossRushing = lastBossD !== null && bd < 8 && lastBossD - bd > 1.5;
      lastBossD = bd;
    }
    // Iteration 42: not at Primus in its Adapt phase (no melee blow there; run-050's three "boss close" dashes left no charge for
    // two of its Adapt Atks - 338 and the last 148).
    const primusCalm = !!primusE && primusStage(threats, primusSt.id === primusE.id ? primusSt : null) === 1;
    if (primusCalm) bossRushing = false;
    const bossNear = bosses.length > 0 && dist(me, bosses[0].position) < 4.5 && !primusCalm;
    // Iteration 26: not for a boss in a phase where it cannot swing (channelled, dazed, in the air - BOSS_PHASES idle): the charges
    // are kept for its red and for the phase's end (Skoll's landing, Dark Moon's hallucinations are red of their own).
    const idlePhase = !!(ph && imm.phase && imm.phase.idle);
    // Iteration 22: not for Belphomet's missile bursts (BURST_SHOT) - run-032: both charges went on "shot incoming" at its first two
    // waves (3, then 8 missiles), the next waves hit 3 x 72 with none left; plan()'s step off the line still sees them.
    // Iteration 33: nor for Infernus's breath jets (breathDashCell below decides those).
    // Iteration 41: nor for the spinning arrows while their stream is known (spinDashCell below decides those).
    // (Iteration 45 left Doom's fireballs out above 20% hp - withdrawn by iteration 46: they do ~300 each.)
    const shotNow = shots.some(s => !s.homing && !BURST_SHOT.test(s.type || '') && !(spin && SPIN_ARROW.test(s.type || '')) && !(br && BREATH.test(s.type || '')) && s.eta < 0.3 && s.miss < s.radius + 0.4) && (bosses.length > 0 || hpPct < 0.5) ||
      burnDash(shots.filter(x => !(spin && SPIN_ARROW.test(x.type || ''))), charges);   // iteration 51: a burning shot, a spare charge
    // Standing in red with no way out on foot: every cell a step can reach (5.5 m) is red too -
    // overlapping circles, or one as big as the Seeker's (5.67 m; its volley put four on the hero in
    // run-001). A dash covers that ground in a fifth of the time and lands in less red (fewer circles,
    // nearer an edge), if not out. Blobs have no timer (left 1 s as read), so areaUrgent fires for
    // them only 4 m deep; this does not wait for that. Strikes count too: they are the same blows
    // (the Seeker's volley is DelayedExplosion strikes now), read at their real size, so "no step
    // gets out" means it. Telegraphs with a fill (circles, slices, boxes) keep to areaUrgent.
    const inBlob = areas.some(a => (a.timeless || isStrike(a)) && areaDepth(me, a, 0.4) > 0);
    // Boxed in by the lava (iteration 15; run-021's death): the grid's reachable cells stop at the lava, so
    // in a dry pocket with Magmadon's Charge drawn over it (an 11.8 m blob, the hero 6 m in) no step and no
    // dash plan() knows gets out, and the hero stood 1.6 s until it landed (185 at 192 hp). A dash crosses
    // the lava unhurt (a displacement) and stops only where the navmesh ends: lavaHop finds the dry cell
    // beyond it with the least red. Only in LavaLand, only standing in red, only when plan()'s own dash
    // does not get out of the red.
    let dashCell = pl.dash, hop = null;
    if (onLavaZone() && pl.inArea && (areaUrgent || inBlob) && ready('Movement') && !(pl.dash && pl.dash.area === 0)) {
      hop = lavaHop(gridCache, me, areas, entities);
      if (hop && (!pl.dash || hop.area < pl.dash.area - 3)) dashCell = hop; else hop = null;
    }
    const trapped = inBlob && pl.inArea && !pl.walkOut && pl.here && dashCell && dashCell.area < pl.here.area;
    // Every red the hero stands in is an untimed blob (no strike, no filling telegraph): see dashChoice.
    const under = areas.filter(a => !a.pool && !a.keepOut && areaDepth(me, a, 0.4) > 0);
    const blobOnly = under.length > 0 && under.every(a => a.timeless);
    // White Night's Cataclysm: into this wave's safe circle and stand in it (cataclysmStep) - before
    // every other move and dash (run-013 circled her, dash ready, while two blows came). Inside it the
    // hero stands still and shoots; no dash moves it out.
    let cataInside = false;
    if (cata.safe) {
      const cs = await cataclysmStep(me, gridCache, ready('Movement'), areas.filter(a => a.pool), traced);
      if (cs === 'moving') { cata.moving = true; lastDir = { x: 0, z: 0 }; stuckFrom = null; await sleep(60); continue; }
      if (cs === 'inside') { cataInside = true; if (cata.moving) { cata.moving = false; await tryPost('/hero/stop'); } await stopDir(); }
    }
    // Nyx's Blackhole coming or on (blackholeMove): straight away from it - no circling, casts or
    // attacks - the dash charges kept for it. Not two dashes within 0.6 s: the next look may still
    // find the hero mid-dash. The walk's radial speed (bhTrack) says whether the pull is winning.
    if (bhs && !bhs.soft) {
      if (bhTrack.on !== bhs.on) { bhTrack.length = 0; bhTrack.on = bhs.on; bhTrack.openAt = !bhs.on ? 0 : nowMs - (typeof bhs.left === 'number' && bh.cfg && bh.cfg.blackholeDuration > 0 ? (bh.cfg.blackholeDuration - bhs.left) * 1000 : 0); }
      bhTrack.push({ t: nowMs, d: bhD });
      while (bhTrack.length > 2 && nowMs - bhTrack[1].t > 350) bhTrack.shift();
      const o = bhTrack[0];
      const vr = nowMs - bhDashAt > 700 && nowMs - o.t > 200 ? (bhD - o.d) / ((nowMs - o.t) / 1000) : null;
      const elapsed = bhs.on && bhTrack.openAt ? (nowMs - bhTrack.openAt) / 1000 : 0;
      const pullAt = bh.curves && bhs.on ? x => pullFrom(bh.curves, bh.cfg && bh.cfg.distanceBounds, bh.cfg && bh.cfg.blackholeDuration, x, elapsed) : null;
      const canDash = ready('Movement') && nowMs - bhDashAt > 600;
      // A timed red under the hero landing soon (Starfall's strikes through the Blackhole): stepped off (bhDodge).
      // Iteration 17: 0.5 m more margin (BH_RED_MARGIN) - run-024: a Starfall 0.25 m outside its drawing with 0.15 s left, the
      // pull dragged the hero 0.3 m into it while it walked out (111.6).
      const bhRed = areas.some(a => !a.keepOut && !a.pool && !a.timeless && a.left < 1.2 && areaDepth(me, a, BH_RED_MARGIN) > 0);
      const bm = blackholeMove({ grid: gridCache, me, b: bhs, vr, pullAt, charges, canDash, urgent: areaUrgent || shotNow, dodge: bhRed, avoid: areas });
      const tag = `${bhs.on ? 'on' : 'coming'}${typeof bhs.left === 'number' ? ' ' + bhs.left.toFixed(1) + 's left' : ''}, ${bhD.toFixed(1)}m from it${vr != null ? `, ${vr >= 0 ? '+' : ''}${vr.toFixed(1)} m/s` : ''}${pullAt ? `, pull ${pullAt(bhD).toFixed(1)} m/s` : ''}`;
      if (bm.dash) {
        bhDashAt = nowMs; bhTrack.length = 0;
        log(`  dash: out of the blackhole (${bm.why}; ${tag}) -> ${dist(bm.dash.p, bhs.centre).toFixed(1)}m from it, ${bm.dash.clear}m of room`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: bm.dash.p.x, z: bm.dash.p.z, move: false });
        if (traced) trace('  dash blackhole ' + bm.why + ' ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0;
        continue;
      }
      const act = bm.walk ? (bm.why === 'stepping off the red' ? 'dodge' : 'walk') : 'hold';
      if (act !== bh.act) { bh.act = act; log(`  blackhole: ${bm.why} (${tag})`); }
      if (traced) trace(`  bh ${act} ${tag}${bm.walk ? ` -> ${dist(bm.walk.p, bhs.centre).toFixed(1)}m` : ''}`);
      if (bm.walk) {
        const w = bm.walk.way.p, wd = dist(me, w);
        const dir = wd < 0.3 ? { x: 0, z: 0 } : { x: (w.x - me.x) / wd, z: (w.z - me.z) / wd };
        if (Math.hypot(dir.x - lastDir.x, dir.z - lastDir.z) > 0.2 || Date.now() - lastDirAt > 300) { await tryPost('/hero/move_dir', dir); lastDir = dir; lastDirAt = Date.now(); }
      } else await stopDir();
      await sleep(30);
      continue;
    }
    bh.act = null; bhTrack.on = undefined;
    // A chaser orb about to touch the hero: a dash straight away from it, the last charge too (its
    // explosion: 138 in run-009, ~490 in run 28). Not twice in 0.6 s.
    for (const o of co.orbs) if (o.id != null && !orbSeen.has(o.id)) orbSeen.set(o.id, nowMs);
    const orbNear = co.orbs.map(o => ({ o, d: dist(me, o.position), age: (nowMs - (orbSeen.get(o.id) || nowMs)) / 1000 })).sort((x, y) => x.d - y.d)[0];
    const orbLate = orbNear && orbNear.age >= ORB_LATE && orbNear.d < ORB_KEEP;
    if (orbNear && (orbNear.d < ORB_DASH || orbLate) && ready('Movement') && nowMs - bhDashAt > 600) {
      const cell = awayCell(gridCache, me, orbNear.o.position, areas);   // iteration 28: its landing off the red too
      if (cell && dist(cell.p, orbNear.o.position) > Math.max(orbNear.d + 2.5, orbLate ? ORB_BLAST : 0)) {
        bhDashAt = nowMs;
        log(`  dash: chaser orb ${orbNear.d.toFixed(1)}m away${orbLate ? `, ${orbNear.age.toFixed(1)}s old (about to blow)` : ''} -> ${dist(cell.p, orbNear.o.position).toFixed(1)}m from it, ${cell.clear}m of room`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: cell.p.x, z: cell.p.z, move: false });
        if (traced) trace('  dash chaser orb ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0;
        continue;
      }
    }
    // Skoll's landing that follows the hero (followMove): straight away from it on foot, a dash straight away late.
    const fol = !cataInside && !(bhs && !bhs.soft) ? followStrike(areas, me) : null;
    if (fol) {
      const fm = followMove(gridCache, me, fol, areas, ready('Movement') && nowMs - dashAt > 600);
      if (nowMs - folSaidAt > 3000) { folSaidAt = nowMs; log(`  ${fol.type}: it follows the hero - walking straight away (${fm.fd.toFixed(1)}m from its centre, lands in ${fol.left}s, dash ${ready('Movement') ? 'ready' : 'not ready'})`); }
      if (fm.dash) {
        log(`  dash: straight away from ${fol.type} (${fm.fd.toFixed(1)}m from its centre, lands in ${fol.left}s) -> ${dist(fm.dash.p, fol.centre).toFixed(1)}m from it, ${fm.dash.clear}m of room`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: fm.dash.p.x, z: fm.dash.p.z, move: false });
        if (traced) trace('  dash follow ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0;
        continue;
      }
      if (fm.walk) {
        if (traced) trace(`  follow walk ${fm.fd.toFixed(1)}m from it, ${fol.left}s left`);
        if (Math.hypot(fm.walk.x - lastDir.x, fm.walk.z - lastDir.z) > 0.2 || Date.now() - lastDirAt > 300) { await tryPost('/hero/move_dir', fm.walk); lastDir = fm.walk; lastDirAt = Date.now(); }
        await sleep(30);
        continue;
      }
    }
    // Iteration 21 (run-031's death): a zone boss that cannot be hurt (its shield, or nothing else to shoot) within
    // SHIELD_REACH - nothing to gain there, its untelegraphed blow to lose (Infernus's Atk at 6.8 m, 78 + the burn, twice,
    // the hero pinned at the arena's edge 6.6-8 m from it for 6 s, "not moving", both charges ready). A dash to the cell
    // farthest from it (awayCell) when it ends SHIELD_AWAY m off at least, 2.5 m farther than now, out of the red: with
    // both charges, or with one when walking has stalled (stuckFrom held 0.6 s) or it is within 6 m.
    // Not at Nyx's short shields (4 s; the charges are for her Blackhole): pillars up, or Infernus.
    // Iteration 35: not from Azurak rolling (its roll pillars make it 'shielded'): it passes at 15-30 m/s - its roll's box is the red.
    const keepBoss = (ct.why === 'shielded' || (ct.why === 'invulnerable' && !shortHold(firstBlock) && /BossInfernus/.test(first.type || ''))) && first && first.monsterType === 'Boss' && !/BossAzurak/.test(first.type || '') ? (ct.shielded || first) : null;
    if (keepBoss && !cataInside && !(bhs && !bhs.soft) && ready('Movement') && nowMs - dashAt > 1200) {
      const kd = dist(me, keepBoss.position);
      const stalled = !!stuckFrom && nowMs - stuckAt > 600;
      if (kd < SHIELD_REACH && (charges == null || charges >= 2 || stalled || kd < 6)) {
        const cell = awayCell(gridCache, me, keepBoss.position, areas);
        const cd = cell ? dist(cell.p, keepBoss.position) : 0;
        if (cell && cell.red === 0 && cd >= Math.max(SHIELD_AWAY, kd + 2.5)) {
          log(`  dash: away from ${keepBoss.name} (${ct.why}, ${kd.toFixed(1)}m off${stalled ? ', walking stalled' : ''}) -> ${cd.toFixed(1)}m from it, ${cell.clear}m of room`);
          const r = await tryPost('/hero/cast', { slot: 'Movement', x: cell.p.x, z: cell.p.z, move: false });
          if (traced) trace('  dash keep off ' + JSON.stringify(r).slice(0, 160));
          lastDirAt = 0;
          continue;
        }
      }
    }
    // Iteration 33: a breath jet that meets the hero where it is going - a dash sideways round Infernus, away from its facing
    // (breathDashCell); none that way (a wall): across the stream to the other side, far enough past it; none: the ordinary
    // dash for a shot about to hit (plan()'s dash cell).
    // Iteration 41: the spinning arrows about to meet the hero (spinMeet within SPIN_DASH_T s) - a dash through the stream, back
    // round the carrier against its turn or out of its reach (spinDashCell): the dash is uncollidable most of the way, and the
    // side just swept stays clear for most of a turn. None that way: the ordinary dash for a shot about to hit.
    let spinPlain = false;
    const spinM = spin ? spinMeet(spin, me) : null;
    if (traced && spin) trace(`  spin ${spin.from} theta ${Math.round(spin.theta)} turn ${spin.turn} w ${Math.round(spin.omega)} v ${spin.speed} reach ${spin.reach.toFixed(1)} paused ${spin.pausedFor} - meets the hero in ${spinM.t === Infinity ? '-' : spinM.t.toFixed(2) + 's'} (${spinM.delta != null ? Math.round(spinM.delta) : '-'} deg, ${spinM.rho != null ? spinM.rho.toFixed(1) : '?'}m)`);
    if (spin && spinM.t <= SPIN_DASH_T && !cataInside && ready('Movement') && nowMs - spinSt.dashAt > 700) {
      const cell = spinDashCell(gridCache, me, spin, areas, shots);
      const tag = `the stream ${Math.round(spinM.delta)} deg off, meeting the hero in ${spinM.t.toFixed(2)}s ${spinM.rho.toFixed(1)}m from ${spin.by}`;
      if (cell) {
        spinSt.dashAt = nowMs;
        log(`  dash: spinning arrows (${tag}) - ${cell.gone ? 'out of their reach' : `back round it ${Math.round(cell.gain)} deg`} -> ${cell.dC.toFixed(1)}m from it, ${cell.clear}m of room | hp ${Math.round(hero.hp)}`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: cell.p.x, z: cell.p.z, move: false });
        if (traced) trace('  dash spin ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0;
        continue;
      }
      spinPlain = true;
      if (!spinSt.noCellSaid) { spinSt.noCellSaid = true; log(`  dash: spinning arrows (${tag}) - no cell back round it, the plain dash | hp ${Math.round(hero.hp)}`); }
    }
    let breathPlain = false;
    if (br && br.hit && infernus && !cataInside && ready('Movement') && nowMs - breath.dashAt > 700) {
      let cell = breathDashCell(gridCache, me, infernus.position, br.side, areas, br.d), how = 'round it';
      if (!cell) { const c2 = breathDashCell(gridCache, me, infernus.position, -br.side, areas, br.d); if (c2 && c2.gain >= Math.abs(br.gap) + BREATH_GAIN) { cell = c2; how = 'across the stream'; } }
      const tag = `${br.el.toFixed(1)}s in, ${Math.round(br.gap)} deg off its facing, ${br.d.toFixed(1)}m from it, the jet ${br.hit.eta}s off (r ${br.hit.radius})`;
      if (cell) {
        breath.dashAt = nowMs;
        log(`  dash: Infernus's breath (${tag}) - ${how}, ${Math.round(cell.gain)} deg -> ${cell.dC.toFixed(1)}m from it, ${cell.clear}m of room | hp ${Math.round(hero.hp)}`);
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: cell.p.x, z: cell.p.z, move: false });
        if (traced) trace('  dash breath ' + how + ' ' + JSON.stringify(r).slice(0, 160));
        lastDirAt = 0;
        continue;
      }
      breathPlain = true;
      if (dashCell && !breath.noCellSaid) { breath.noCellSaid = true; log(`  dash: Infernus's breath (${tag}) - no cell round it, the plain dash | hp ${Math.round(hero.hp)}`); }
    }
    // Blind boss (dashChoice): the red the hero stood in, and the boss's blows since the last look.
    if (bossFight) {
      if (areas.some(a => !a.pool && !a.keepOut && areaDepth(me, a, 0.7) > 0)) blind.redAt.push(nowMs);
      // Iteration 22: kept 4 s, not 1.5 - run-032: the hits were checked 1.5 s late (the lava walk-off skipped this block), the
      // red 0.3-0.7 s before the Eruption had been dropped, and "no red under the hero before it" was logged (it stood in it).
      while (blind.redAt.length && nowMs - blind.redAt[0] > 4000) blind.redAt.shift();
      const boss = bosses.find(e => e.monsterType === 'Boss');
      for (const { t, h } of blind.queue.splice(0)) {
        if (!h || !boss || h.caster !== boss.name || h.overTime || SHOT_HIT.test(h.by || '') || !(h.amount >= BLIND_HIT * hero.maxHp)) continue;
        if (blind.redAt.some(r => r < t - 300 && r > t - 1600)) continue;   // red >= ~0.1-0.5 s before (hits are read every 0.4 s)
        blind.hits++;
        log(`  blind boss: ${Math.round(h.amount)} from ${h.by} with no red under the hero before it - the last dash charge is not kept at ${boss.name} (${blind.hits})`);
      }
    } else blind.queue.length = 0;
    // Iteration 39: Primus without the mod's readers of its blows is blind too (primusBlind).
    const blindBoss = bossFight && (bosses.some(e => BLIND_BOSSES.test(e.type || '')) || blind.hits > 0 || primusBlind(threats, bosses));
    // Red about to land and the dash lands in red too: not taken (a walk may do better) - unless no step
    // gets out (trapped), where less red is the best there is (iteration 15: run-021 stood 1.6 s, 6 m deep in
    // a blob no walk could leave, a charge ready for the last 0.8 s of it, because of this rule).
    // Iteration 30: "on top of us" only while the nearest one is not falling behind (closing), a soft dash only where it lands
    // clear (softLanding), "low health" within melee reach (SOFT_NEAR, was 5 m).
    const nearE = entities.reduce((b, e) => !b || dist(me, e.position) < dist(me, b.position) ? e : b, null);
    const nearD = dist(me, nearE.position), onTop = nearD < 2.2 && closing(lastNear && nowMs - lastNear.t < 800 ? lastNear : null, nearE.id, nearD);
    lastNear = { id: nearE.id, d: nearD, t: nowMs };
    const dc = !cataInside && ready('Movement') && dashCell && !(dashCell.area > 0 && areaUrgent && !trapped) ? dashChoice({
      areaUrgent, trapped, shotNow: shotNow || breathPlain || spinPlain, onTop, cornered, bossNear: bossNear && !idlePhase && !br, bossRushing: bossRushing && !idlePhase && !br, lowClose: hpPct < 0.35 && close < SOFT_NEAR,
      landWorse: softLanding(dashCell, me, entities),
      bossFight, charges, blobOnly, blind: blindBoss, keepOne: onLavaZone(),
    }) : null;
    // Iteration 44: breaking out (pin.on) - the dash along the way to the open ground (breakPlan's 'dash', one charge will do), or
    // a soft dash taken there rather than to plan()'s cell. A hard reason (the red, a shot) dashes as before. Once a break-out.
    // Not the last charge in Primus's Adapt phase (iteration 42: its Adapt Atk wants the charges; no soft dashes there).
    const pinDash = pin.on && !pin.on.dashed && !cataInside && ready('Movement') && nowMs - pin.dashAt > 800 && !(dc && dc.kind === 'hard') && !(primusCalm && charges != null && charges < 2) ?
      (pin.on.how === 'dash' ? pin.on.to : dc && dc.kind === 'soft' ? pin.on.oa.dashTo : null) : null;
    if (pinDash) {
      pin.dashAt = nowMs; pin.on.dashed++; pin.replan = true;
      if (pin.on.how !== 'dash') pin.on.hows.push('dash');
      log(`  dash: breaking out (${pin.on.how === 'dash' ? pin.on.why : dc.why}) -> (${pinDash.x.toFixed(1)}, ${pinDash.z.toFixed(1)}), ${dist(me, pinDash).toFixed(1)}m along the way to the open ground (${Math.round(pin.on.oa.area)} m2) | dash ${charges ?? '?'} charges`);
      const r = await tryPost('/hero/cast', { slot: 'Movement', x: pinDash.x, z: pinDash.z, move: false });
      if (traced) trace('  dash break-out ' + JSON.stringify(r).slice(0, 160));
      lastDirAt = 0; gridAt = 0;
      continue;
    }
    if (dc && dc.kind === 'held') {
      if (nowMs - heldLogAt > 3000) { heldLogAt = nowMs; log(`  dash held: ${dc.why} - ${dc.walk ? `walking (${dc.walk})` : `keeping the last charge for the red`}`); }
      if (traced) trace('  dash held ' + dc.why + (dc.walk ? ' / walk: ' + dc.walk : ''));
    } else if (dc) {
      const why = dc.why === 'area' ? `in a ${areaEscape.shape} of ${areaEscape.by || '?'} (${areaEscape.depth.toFixed(1)}m in, lands in ${areaEscape.left}s${areaEscape.slowUrgent ? `, walking ${areaEscape.v.toFixed(1)} m/s` : ''})` : dc.why === 'trapped' ? `no way out of the red on foot (${areas.filter(a => areaDepth(me, a, 0) > 0).length} areas)` : dc.why === 'cornered' ? `cornered (room ${pl.here.clear}m)` : dc.why;
      fireSeen.dashAt = Date.now(); fireSeen.dashWhy = why;
      log(`  dash: ${why} -> cell with ${dashCell.clear}m of room, ${dashCell.md.toFixed(1)}m away${hop ? `, across the lava (${hop.lava.toFixed(1)}m of it; red there ${Math.round(hop.area)}, here ${Math.round(pl.here ? pl.here.area : 0)})` : ''}`);
      const r = await tryPost('/hero/cast', { slot: 'Movement', x: dashCell.p.x, z: dashCell.p.z, move: false });
      if (traced) trace('  dash ' + why + (hop ? ' (across the lava)' : '') + ' ' + JSON.stringify(r).slice(0, 160));
      lastDirAt = 0; if (hop) gridAt = 0;
      continue;
    }

    // Walk toward the chosen cell along the grid's path, shooting all the while.
    // Traced (iteration 15): the walk's decision - run-021's hero stood 1.6 s in a blob and the trace did not
    // say whether plan() chose to stay, had no cell, or walked and did not move.
    if (traced && (pl.inArea || !pl.best)) trace(`  walk ${pl.best ? `-> ${pl.way.p.x.toFixed(1)},${pl.way.p.z.toFixed(1)} (${dist(me, pl.way.p).toFixed(1)}m${pl.way.pulled ? ', pulled' : ''}; best ${pl.best.md.toFixed(1)}m red ${Math.round(pl.best.area)}${pl.best.cross ? ` crossing ${Math.round(pl.best.cross)}` : ''})` : 'none: no cell'} here red ${pl.here ? Math.round(pl.here.area) : '?'} room ${pl.here ? pl.here.clear : '?'}, walkOut ${pl.walkOut}${walkV != null && walkV < 3.5 ? ` (walking ${walkV.toFixed(1)} m/s)` : ''}, trapped ${!!trapped}, dash cell ${dashCell ? `${dashCell.md.toFixed(1)}m red ${Math.round(dashCell.area)}` : 'none'}${onLavaZone() ? `, on lava ${hero.onHazard}` : ''}${planOpts.approach ? ', approach' : ''}${planOpts.momentum ? ', momentum' : ''}`);
    // The last steps to the shooting place (plan()'s cells are 2 m off at the least): straight at it.
    if (planOpts.approach && !pl.inArea && !cataInside && Date.now() - meteorAt > 2500 && dist(me, planOpts.approach) < 2.2) {
      const ad = dist(me, planOpts.approach);
      const dir = ad < (doomSt.spot ? DOOM_STOP : 0.4) ? { x: 0, z: 0 } : { x: (planOpts.approach.x - me.x) / ad, z: (planOpts.approach.z - me.z) / ad };
      if (Math.hypot(dir.x - lastDir.x, dir.z - lastDir.z) > 0.3 || Date.now() - lastDirAt > 400) { await tryPost('/hero/move_dir', dir); lastDir = dir; lastDirAt = Date.now(); }
    } else if (lavaHeld && !pl.inArea && !cataInside && !shots.some(x => typeof x.eta === 'number' && x.eta < 1.2 && x.miss < x.radius + 1.5)) {
      await stopDir();
    } else if (pl.best && !cataInside) {
      const w = pl.way.p;
      const wd = dist(me, w);
      const dir = wd < 0.5 ? { x: 0, z: 0 } : { x: (w.x - me.x) / wd, z: (w.z - me.z) / wd };
      const changed = Math.hypot(dir.x - lastDir.x, dir.z - lastDir.z) > 0.3;
      if (changed || Date.now() - lastDirAt > 400) {
        await tryPost('/hero/move_dir', dir);
        lastDir = dir; lastDirAt = Date.now();
      }
    }

    // Skills on the move; the charged shot only with nothing on top of us.
    let casted = false;
    // Iteration 18: one skill at a time - a quick cast (castQuick) still in the server's hands holds the next.
    const castFree = !castBusy.p || Date.now() - castBusy.since > 1500;
    // Iteration 32: who a skill may be spent on (alive, can be hurt now), everyone in sight (the hold's "something better"),
    // their pace (the circles' lead). The aim is chosen by castPolicy except at a zone boss and at a shielded boss's pillars.
    const okPool = entities.filter(e => e && e.alive !== false && !cannot(e) && effHp(e) > 0);
    const okSeen = entities.filter(e => e && e.alive !== false && effHp(e) > 0);
    notePace(paceSeen, okPool, nowI);
    const okFree = !bossFight && ct.why !== 'shielded';
    // Precision Shot's full charge (P_FULL; run-031 read its numbers: damage 1.5ap -> 8ap and the stun 0.5 -> 2.5 s over the
    // charge, the cooldown 9.4-10 s - so a cast is worth charging, not repeating) whenever standing ~1 s is safe: nobody within
    // P_FAR_CLEAR m, no red within 3 m, no shot about, no meteor/Starfall rain in the last 2.5 s, hp >= 50%, the boss not
    // rushing in. Iteration 32: and in rooms only where the extra damage lands (P_FULL_GAIN).
    const pNormCap = bosses.length ? P_CAP_BOSS : P_CAP;
    const pFullOk = P_FULL && close >= P_FAR_CLEAR && hpPct >= 0.5 && Date.now() - meteorAt > 2500 && !shots.length && !bossRushing && !dasher &&
      !areas.some(a => !a.pool && areaDepth(me, a, 3) > 0);
    for (const s of castFree ? ['R', 'W', 'E', 'Q'] : []) {
      const k = skill(s);
      if (!ready(s)) continue;
      // Mass Cleanse on the hero while it burns (SELF_CLEANSE): the burn off and a heal for it.
      const selfCleanse = SELF_CLEANSE.test(k.type) && burning(hero);
      // Iteration 45: none at Primus while its Force phase is paced (primusPace) - but the burn put out.
      if (paceHold && !selfCleanse) continue;
      // Not at an invulnerable target: the cooldowns are kept for when it can be hurt (chooseTarget; iteration 26: any hurtBlock).
      if (cannot(target) && !selfCleanse) continue;
      const kRange = k.trigger.range || 9;
      const isPrecision = /PrecisionShot/.test(k.type);
      // Iteration 32: the aim and whether a big skill is worth it now (castPolicy). aimE: the enemy it goes at (null: a point,
      // aimP); dA: how far the aim is. Held: the slot's hold is noted (said once, timed for OK_HOLD_MAX and the summary).
      let aimE = target, aimP = null, okd = null, okDmg = null;
      // Iteration 44: breaking out by killing the weak add in the way - the skill goes at it, not where castPolicy weighs best.
      if (!selfCleanse && okFree && !(pin.on && pin.on.how === 'kill')) {
        const sh = skillShape(k, shapeCM.get(k.type), LUNGES.has(k.type) || CHARGES.test(k.type));
        sayShape(sh, k);
        okDmg = skillDmg(k, hero, isPrecision ? Math.min(pNormCap, 0.45) : 0);
        const reach = isPrecision ? precisionReach(precision.cfg, pFullOk ? P_CAP_FAR : pNormCap) :
          sh.kind === 'circle' ? kRange : sh.kind === 'cone' ? (sh.r || kRange) : sh.kind === 'around' ? sh.r : Math.max(kRange, 4) + 1.5;
        const hold = okHold.get(s), heldFor = hold && hold.type === k.type && nowI - hold.lastAt <= OK_HOLD_GAP ? (nowI - hold.since) / 1000 : 0;
        okd = castPolicy({ sh, dmg: okDmg, big: bigSkill(k, okDmg), me, pool: okPool, seen: okSeen, reach, target, heldFor });
        if (!okd.cast) {
          if (okd.best && bigSkill(k, okDmg)) noteHold(okHold, okStat, s, k, okd, okDmg, nowI, hero);
          continue;
        }
        if (okd.best) { aimE = okd.best.e || null; aimP = okd.best.e ? null : okd.best.p; }
      }
      const dA = aimE ? dist(me, aimE.position) : aimP ? dist(me, aimP) : d;
      // Iteration 20: Precision Shot charged for the distance (precisionCharge); at a boss not within P_BOSS_MIN_D, nor
      // with red under or next to the hero, a shot about to land or the boss rushing in - the loop waits out the charge.
      let pCharge = 0.3, pFar = false;
      if (isPrecision && !selfCleanse) {
        if (close < 4) continue;
        if (!aimE) { aimE = target; aimP = null; }
        // Iteration 26: not into a big shield on a zone boss (bigShield: Nyx's decaying one after her phase change).
        // Iteration 31: not held on HarmlessWhispers's armor shield (armorShield) - it does not decay, and breaking it stuns.
        const armor = bigShield(aimE) && armorShield(lucidActive, aimE.effects || (imm.id === aimE.id ? imm.effects : null));
        if (armor && !imm.armorSaid) { imm.armorSaid = true; log(`  precision shot: into ${aimE.name}'s armor shield ${Math.round(aimE.shield)} (HarmlessWhispers: it does not decay, its break stuns) | hp ${Math.round(hero.hp)}`); }
        if (bigShield(aimE) && !armor) {
          if (!imm.shieldSaid) { imm.shieldSaid = true; log(`  precision shot: held - ${aimE.name}'s shield ${Math.round(aimE.shield)} (${Math.round(100 * aimE.shield / aimE.maxHp)}% of max) would take it | hp ${Math.round(hero.hp)}`); }
          continue;
        }
        imm.shieldSaid = false;
        // Iteration 21: the far shot (P_CAP_FAR, ~14.6 m) at a pillar that must die, or at a LavaLand target with lava on
        // the way (shield.lavaWay; run-030's Scarabs across it) - only with nobody within P_FAR_CLEAR m, no red within 3 m
        // and the health to stand ~1 s.
        const farShot = (ct.why === 'shielded' && MUST_KILL.test(aimE.type || '')) || (onLavaZone() && !bosses.length && shield.lavaWay.get(aimE.id) === false);
        // Iteration 32: in rooms the full charge only where its extra damage lands - a line through the same enemies at the
        // full charge's damage worth P_FULL_GAIN x the short one's (run-040's two full shots into 34-60 hp Scarabs).
        let fullOk = pFullOk;
        if (fullOk && okd && okd.best && okDmg != null) {
          const full = skillDmg(k, hero, P_CAP_FAR), short = okd.best;
          // Kept when a normal charge would not reach it at all (as before).
          fullOk = (full != null && aimValue(short.hits, full).value >= P_FULL_GAIN * short.value) || precisionCharge(dA, precision.cfg, pNormCap) == null;
        }
        let pc = precisionCharge(dA, precision.cfg, farShot || fullOk ? P_CAP_FAR : pNormCap);
        if (pc == null) continue;
        if (fullOk) pc = P_CAP_FAR;
        // Iteration 46 (run-052's 434): at Primus in its Adapt phase no longer than its next Adapt Atk allows (adaptCap) - none
        // while one is coming, while the Arbalest aims, or before the phase's first one is seen.
        if (primusE && primusStage(threats, primusSt.id === primusE.id ? primusSt : null) === 1) {
          const cap = adaptCap(adaptSt, Date.now(), ready('Movement'));
          const c2 = cap >= P_MIN ? (pc <= cap ? pc : precisionCharge(dA, precision.cfg, cap)) : null;
          if (c2 == null) {
            if (Date.now() - capSaidAt > 5000) { capSaidAt = Date.now(); log(`  precision shot: held at Primus - ${adaptSt.casting ? 'its Adapt Atk is coming' : adaptSt.arbalest ? 'its Arbalest aims' : adaptSt.lastAt == null ? 'no Adapt Atk seen yet this phase' : `its next Adapt Atk due, a charge of ${cap}s at most`} (${pc}s wanted at ${dA.toFixed(1)}m) | hp ${Math.round(hero.hp)}`); }
            continue;
          }
          pc = c2;
        }
        if (pc > pNormCap && !fullOk && (close < P_FAR_CLEAR || hpPct < 0.4 || areas.some(a => !a.pool && areaDepth(me, a, 3) > 0))) continue;
        if (bosses.length && (dist(me, bosses[0].position) < P_BOSS_MIN_D || bossRushing)) continue;
        // Red under or next to the hero, or a shot about to land: not while the loop waits out a charge longer than the old
        // 0.3 s, nor at a boss at all (in rooms a 0.3 s charge goes as before).
        const pDanger = areas.some(a => !a.pool && areaDepth(me, a, 1) > 0) || shots.some(x => typeof x.eta === 'number' && x.eta < 1);
        if (pDanger && (bosses.length || pc > P_MIN)) continue;
        // Iteration 41: within a dasher's reach no charge over P_MIN (run-049: 0.95 s at 8.7 m from the Phase Bug - its dash came
        // in that second, 280); nor one the spinning arrows would meet before the hero moves again.
        if (dasher && pc > P_MIN) { const c2 = precisionCharge(dA, precision.cfg, P_MIN); if (c2 == null) continue; pc = c2; }
        if (spin && spinMeet(spin, me).t < pc + 0.5) continue;
        pCharge = pc; pFar = farShot && dA > precisionReach(precision.cfg, pNormCap);
      } else {
        if (!selfCleanse && (k.trigger.aim === 'None' ? d > 8 : dA > Math.max(kRange, 4) + 1.5)) continue;
        if (s === 'R' && (close < 4 || (bosses.length && dA < 6))) continue;
      }
      if (COSTS_HEALTH.has(k.type)) continue;
      // A skill that holds the hero in place (heavy: iteration 19, run-027's Doomsday Meteor next to Skoll) - only with
      // no boss, nobody within HEAVY_CLEAR, no red, no shot, and the health to spare.
      if (heavy(k.type) && (bosses.length || close < HEAVY_CLEAR || areas.some(a => !a.pool && !a.keepOut) || shots.length || hpPct < 0.5)) continue;
      // Skills that carry the hero onto the target (LUNGES) - not at a zone boss, nor when hurt.
      if (LUNGES.has(k.type) && (bossFight || hpPct < 0.5)) continue;
      // Bone Crusher (CHARGES): never in LavaLand, elsewhere as a lunge. A lunge in LavaLand only onto a
      // target on standable ground with room (lungeOk) - run-019: a Searing Charge onto a Magmadon, a lava tick in the same 3 s.
      if (CHARGES.test(k.type) && (onLavaZone() || bossFight || hpPct < 0.5)) continue;
      if (LUNGES.has(k.type) && onLavaZone() && !lungeOk(gridCache, (aimE || target).position)) continue;
      // A memory that cannot be cast at an enemy: not tried again (run-010: Ice Shield, St_C_IceBlock,
      // refused "is not a valid target for this skill" 152 times in the boss trace - a wasted call a tick).
      const nkE = aimE || target;
      const nk = k.type + (isBossE(nkE) ? ' boss' : '');
      if (NOT_AT_ENEMIES.has(nk)) continue;
      // The charged shot's numbers, read once while it charges (precisionNumbers).
      const peekP = isPrecision && !precision.cfg && precision.tries < 3 ? sleep(150).then(precisionNumbers) : null;
      const body = selfCleanse ? { slot: s, x: me.x, z: me.z, move: false } :
        aimP ? { slot: s, x: aimP.x, z: aimP.z, charge: pCharge, move: false } : { slot: s, target: (aimE || target).id, charge: pCharge, move: false };
      const quick = QUICK_CASTS && !selfCleanse && !peekP && !/PrecisionShot/.test(k.type) && !CHARGING.has(k.type);
      const r = quick ? await castQuick(body, k.type, nk, castBusy) : await tryPost('/hero/cast', body);
      if (selfCleanse && !r.error) log(`  ${k.type}: cast on the hero to put the burn out - hp ${Math.round(hero.hp)}`);
      if (peekP) { await peekP; if (precision.cfg) log(`  precision shot: its numbers ${JSON.stringify(precision.cfg)} - charged ${pCharge} s at ${dA.toFixed(1)} m; attack range ${range} m`); }
      if (isPrecision && !r.error && pFar) log(`  precision shot: a far shot at ${aimE.name} @${dA.toFixed(1)}m, charged ${pCharge}s${ct.why === "shielded" ? " (the boss shielded)" : " (lava on the way)"} | hp ${Math.round(hero.hp)}`);
      if (isPrecision && !r.error) { pStat.n++; pStat.dMax = Math.max(pStat.dMax, dA); pStat.cSum += pCharge; if (!precision.dmg) precisionDamage().catch(() => {}); }
      if (r.error && /not a valid target/.test(r.error)) { NOT_AT_ENEMIES.add(nk); log(`  ${k.type}: not a valid target for it (${nkE.name}) - not cast at ${isBossE(nkE) ? 'bosses' : 'enemies'} again`); }
      // Iteration 32: the cast as weighed - a hold ended, an area hitting several, a big skill's landing counted.
      if (!r.error && okd && okd.best) noteCast(okHold, okStat, s, k, okd, isPrecision ? skillDmg(k, hero, pCharge) : okDmg, nowI, hero, bigSkill(k, okDmg));
      // Traced: whether a cast in the red holds the hero there (run-003 stood a whole second in
      // Skoll's AuraBlade box, and it was not seen what it was doing meanwhile).
      if (traced) trace(`  cast ${s} ${k.type}${isPrecision ? ` ${pCharge}s @${dA.toFixed(1)}m` : ''}${aimE && aimE !== target ? ` at ${aimE.name} ${Math.round(effHp(aimE))}hp` : aimP ? ` at (${aimP.x.toFixed(1)}, ${aimP.z.toFixed(1)})` : ''}${r.quick ? ' (quick)' : ''}${pl.inArea ? ' (in red)' : ''}${r.error ? ' - ' + r.error.slice(0, 60) : ''}`);
      // Iteration 37: the seconds this cast holds the hero (Precision Shot: the charge sent + its cast daze; another charged
      // skill: its charge; the rest: the channel read by learnLock) - a memory's impact counts them as a cost.
      if (!r.error && !selfCleanse) tallyChan(k.type, isPrecision ? pCharge + ((precision.cfg && precision.cfg['channel.castDazeDuration']) ?? 0.1) :
        CHARGING.has(k.type) ? pCharge : ((skillLocks.get(k.type) || {}).lock || 0));
      if (!r.error) { casted = true; if (!selfCleanse) tallyCast(k.type); break; }
    }
    if (casted) { lastDirAt = 0; continue; }

    if (Date.now() - propsAt > 1000) {
      propsCache = (await get('/entities', { kind: 'props', radius: 10 })).entities.filter(p => p.alive && /Stone_(Gold|DreamDust|Nightmare)/.test(p.type));
      propsAt = Date.now();
    }
    const deposit = d > range * 1.05 ? propsCache.find(p => dist(me, p.position) <= range) : null;
    // Iteration 26: no basic attack at one that cannot be hurt now (chooseTarget found nothing else that can) - a deposit in
    // reach still, since it breaks. The last attack's target and time feed the frozen-hp fallback.
    let atk = attackPick(target, deposit, cannot);
    // Iteration 45: at Primus while its Force phase is paced, only the hits primusPace allows (its budget, its clock's keep-alive).
    const paced = paceHold && !!atk && atk.id === primusE.id && !pace.atk;
    if (paced) atk = null;
    if (atk) await tryPost('/hero/attack_in_place', { target: atk.id });
    if (atk && atk === target) { atkAt = Date.now(); atkId = target.id; imm.atkSaid = null; }
    else if (!atk && !paced && imm.atkSaid !== target.id) { imm.atkSaid = target.id; log(`  no attacks: ${target.name} cannot be hurt now (${blockOf(target)}) and nothing else in sight can - holding them | hp ${Math.round(hero.hp)}`); }
    await sleep(30);
  }
  return 'timeout';
}

// ----- looting ------------------------------------------------------------------------------
// Until the item is picked up: in the hero's hand (an essence, or a memory with no free slot - its
// owner stays null while it is only held, so the owner alone never said so and every such pickup
// sat out the full 8 s), owned (put on straight away), or gone.
async function waitGone(id, type, seconds = 8) {
  const t = Date.now();
  while (Date.now() - t < seconds * 1000) {
    if (await enemyNear()) return false;
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    const held = st.hero && st.hero.holding;
    if (held && (!type || held.type === type)) return true;
    try { const on = await get('/reflect/get', { path: `#${id}.owner` }); if (on !== null) return true; } catch { return true; }
    await sleep(250);
  }
  return false;
}

// Essences that take health each time their skill is cast. Gem_E_Overload (decompiled): every
// cast of its skill starts Se_HealthCost for healthRatio of max health - in run-001 it was socketed
// into E in zone 1 and drained ~950 hp over 4 min (58-107 a cast); the hero reached the Seeker at
// 38% and died there. The bot casts every skill on cooldown and has no way to heal, so never wear one.
// Memories too: St_R_Immolation (decompiled Se_R_Immolation) takes sacrificedHpRatio of the hero's
// current health on every cast, as pure damage in 5 ticks that ignore armour, shields and damage
// immunity. run-005 took it from a Retrospection Remnant in zone 0's first room (it went into a free
// slot on pickup) and cast it on cooldown: 552 hp over the run (57 + 42 at 432 hp in one room), and
// its ticks were among the last hits in zone 1, where the room's Aura of Pain (which stops at 1 hp)
// had drained the hero. Never taken from a shrine, bought, picked up or cast; dismantled if held.
const COSTS_HEALTH = new Set(['Gem_E_Overload', 'St_R_Immolation']);

// Memories whose cast carries the hero onto the target (decompiled, history/it6/skills): Vile
// Strike (Se_E_VileStrike: invulnerable for the dash, then a blow where it lands - next to the
// target, vulnerable), Lightning Dance (Se_R_LightningDance: to 0.4 m past each target). The bot
// casts at the target, so at a boss this lands the hero in its melee: run-005's Belphomet, 7.5 m ->
// 0.9 m into its 8 homing missiles, 395 -> 74 hp in 0.4 s - the hp it left zone 0 with (86/492).
// Not cast at a zone boss, nor below half health.
// And the rushes (decompiled history/it9/skills - the ability moves its caster with StartDisplacement
// forward along the aim, or onto the target): Stygian Rush (Ai_E_StygianRush_Rush: forward up to its
// charged length), Searing Charge, Justice Guillotine, Chomp, Lunge, Fleche. run-009: Stygian Rush
// (W) took the hero from 8.2 to 3.0 m of Nyx, and from 9.5 to 4.4 m of her at the moment she began
// her Blackhole at the arena centre (1026 -> 147 in the 2 s after); both times the next look spent
// a dash on "boss close".
// Mass Cleanse (Ai_E_MassCleanse, history/it13): an InstantDamageInstance at the cast point that, for every
// ally in its range, destroys each ElementalStatusEffect - the burn, Se_Elm_Fire, is one - and heals
// healPerElemental per effect removed. The bot cast it at the enemy (run-017: 9 casts at Infernus, the
// hero never in its range) while the burn did 336 of the ~1000 lost at Infernus (476 over the run).
const SELF_CLEANSE = /^St_E_MassCleanse$/;
// Iteration 21: out of a fight too (loot) - run-030 left Magmadon's room burning at 5 stacks (12.6 every 0.25 s, ~176 in
// 3.5 s after the last relight: Se_Elm_Fire decays all at once decayTime x 0.7 after the last fire hit; walking or standing
// makes no difference). Cast at the hero's own feet.
async function cleanseSelf(hero) {
  const k = ((hero && hero.skills) || []).find(s => SELF_CLEANSE.test(s.type || '') && s.trigger && s.trigger.canCast);
  if (!k) return false;
  const r = await tryPost('/hero/cast', { slot: k.slot, x: hero.position.x, z: hero.position.z, move: false });
  if (!r.error) log(`  ${k.type}: cast on the hero to put the burn out (out of the fight) - hp ${Math.round(hero.hp)}`);
  return !r.error;
}
// Iteration 19: Shadow Walk (history/it19/Ai_R_ShadowWalk.cs: dash.ApplyByDirection along the aim, then shots from where it
// ends). run-028 cast it at bosses 10 times: 5 m -> 0.7-1.6 m of the boss in 7; at White Night 5.0 -> 0.8 m, and her basic
// attack (AtkInstance, no telegraph) hit 274 at 1.06 m 0.3 s later.
const LUNGES = new Set(['St_E_VileStrike', 'St_R_LightningDance', 'St_E_StygianRush', 'St_E_SearingCharge', 'St_E_JusticeGuillotine', 'St_R_Chomp', 'St_Q_Lunge', 'St_Q_Fleche', 'St_R_ShadowWalk']);
// Bone Crusher (iteration 14; decompiled history/it14/Se_R_BoneCrusher.cs): a ChargingChannel - the hero
// runs forward along the aim, 2 -> 20 m/s, unstoppable, sweeping up whom it meets, until the navmesh ends
// 1 m ahead (NavMesh.Raycast) - and lava is navmesh - then an explosion and an 80% slow for 1 s. Not a
// lunge that stops at the target: it goes to the next wall. run-019 took it 16 s before the death; its
// Se_R_BoneCrusher_UnstoppableAndShield was on the hero through the fatal stay in the lava, the target
// 3.7 m off before it and 11.4 m after. Not cast in LavaLand, elsewhere as a lunge; not taken in place of
// another memory (sortHands).
const CHARGES = /^St_R_BoneCrusher$/;

// --- essence fit (iteration 23) ---
// Until iter-22 an essence went into the first memory with a free socket, whatever it does there. The mod
// AreMyGemsCompatible knows whether an essence can ever fire in a memory (Verdict.For: Fine or Dead - e.g. an
// essence that fires on healing in a memory that heals nothing, or Essence of Frost in a memory dealing only
// Fire), and DevTools now asks it by reflection: /hero/fit (for the essence in hand, one by id, or a type as a
// merchant or a shrine names it) answers for each worn memory with sockets, and /hero lists `fit` on each
// socketed essence. Without that mod (or an older DevTools) the verdicts are null and nothing changes.
// The choice: a live socket (not Dead) in the main damage memory first - R, the one the wells and the boss
// soul upgraded (Precision Shot; until iteration 37 - now by impact, upgradeCands) - then the rarest, the highest level; a Dead one never. None live: dismantled
// (holding an item blocks travel; no route takes a socketed essence out, so nothing is moved later either).
// Iteration 25: superseded - planHeld / offerClass below decide (dead ones are kept, paired and moved); chooseSocket and
// fitClass stay as the plain order (tests/iter23).
const FIT_ORDER = ['R', 'W', 'E', 'Q', 'Identity', 'Movement'];
const FIT_RANK = { Common: 0, Rare: 1, Epic: 2, Legendary: 3, Unique: 3, Character: 0 };
const fitVerdict = s => (s && s.fit && s.fit.verdict) || null;
const fitWhy = s => { const w = s && s.fit && s.fit.why; return w ? String(w).split(' | ')[0] : ''; };
// The worn memories with sockets, as /hero/fit lists them; from /hero alone when there is no answer (Q W E R, as before).
function fitSlots(hero, fit) {
  if (fit && Array.isArray(fit.slots)) return fit.slots;
  return ((hero && hero.skills) || []).filter(k => k.type && ['Q', 'W', 'E', 'R'].includes(k.slot) && k.sockets > 0)
    .map(k => ({ slot: k.slot, memory: k.type, rarity: k.rarity, level: k.level, sockets: k.sockets, free: Math.max(0, k.sockets - (k.gems || []).filter(g => g.type).length), fit: null }));
}
// Where the held essence goes: { slot, index, verdict } | { none: 'dead', dead: [slots] } (free sockets, all Dead) |
// { none: 'full' } (no free socket at all).
function chooseSocket(hero, fit) {
  const open = fitSlots(hero, fit).filter(s => s.free > 0);
  const live = open.filter(s => fitVerdict(s) !== 'Dead');
  if (!live.length) return open.length ? { none: 'dead', dead: open } : { none: 'full' };
  const order = s => { const i = FIT_ORDER.indexOf(s.slot); return i < 0 ? 99 : i; };
  live.sort((a, b) => (b.slot === 'R') - (a.slot === 'R') || (fitVerdict(b) === 'Fine') - (fitVerdict(a) === 'Fine') ||
    (FIT_RANK[b.rarity] ?? 0) - (FIT_RANK[a.rarity] ?? 0) || (b.level || 0) - (a.level || 0) || order(a) - order(b));
  const s = live[0];
  const k = ((hero && hero.skills) || []).find(x => x.slot === s.slot);
  const used = new Set(((k && k.gems) || []).filter(g => g.type).map(g => g.index));
  let index = 0; while (used.has(index)) index++;
  return { slot: s.slot, index, verdict: fitVerdict(s), memory: s.memory };
}
// For a merchant's or a shrine's essence: 'free' (live in a memory with a free socket), 'full' (live only where
// the sockets are taken), 'dead' (Dead in every worn memory with sockets), null (no verdict to go by).
function fitClass(fit) {
  if (!fit || !Array.isArray(fit.slots) || !fit.slots.length) return null;
  if (fit.slots.every(s => fitVerdict(s) === null)) return null;
  const live = fit.slots.filter(s => fitVerdict(s) !== 'Dead');
  if (!live.length) return 'dead';
  return live.some(s => s.free > 0) ? 'free' : 'full';
}
// --- end essence fit ---
// --- essence value (iteration 29) ---
// The user: does an essence go "where it is most useful" or "the first slot where it works"? Until now the second:
// planHeld ranked sockets by how many essences fire (the held one + the Dead ones it wakes), then R first, Fine over
// unknown, the rarer memory. It never compared how MUCH an essence gives in one memory against another. Now it does:
// value(essence, memory) = how often the essence's trigger comes in that memory x what one trigger is worth there.
// - The essence's trigger (ESS_KIND, from the game's own descriptions - RawData/en-US/essences.json - and the decompiled
//   Gem_* classes, history/it29/gems): h = each damage event the memory deals (Gem.OnDealDamage /
//   ActorEvent_OnDealDamage), p = the same weighted by the hit's proc coefficient (Charcoal, Mortality, Abyss, Rigidity,
//   Night Sky), c = each cast (OnCastComplete), e = an empowered cast every N s (IsReady/StartCooldown: Talc, Shatter,
//   Responsibility, Rejuvenation...), d = a share of the memory's damage (a dealtDamageProcessor amp, a conversion of the
//   damage dealt, haste - more casts - or kills), l = the memory's heals/barriers, o = nothing about the memory (the
//   hero's attacks, damage taken, stats). A number is its own cooldown ("every 8 s", "once every 3 s"); s = what one
//   trigger gives grows with the damage of the hit (h) or of the cast (e).
// - The memory's use (memUse): casts, hits, proc-weighted hits and damage a minute. Seen by DevTools' /hero/fit
//   (slots[].use - MemoryUse listens to the memory's own cast and damage events, the ones an essence there hears) once
//   every compared memory has 3+ casts and 30+ s of combat; else estimated: casts a minute by the bot's own casts in
//   fights (castTally) when every one has 3+, else 60 / cooldown (the live one from /hero, else MEM_PRIOR's from the
//   game's RawData memories.json); hits = casts x hits a cast x enemies a hit (MEM_PRIOR, from the decompiled ability
//   instances: history/it29/mem), damage per hit its AP/AD multiple.
// So: an on-hit essence goes to the memory that hits most often or most enemies (Incendiary Rounds: 4 hits a cast; a
// piercing or bouncing memory), an on-cast one to the short cooldown, a damage-scaling one to the memory that deals
// the most (Precision Shot: 1.5-8 AP a hit), a capped one (a cooldown of its own) to the biggest hits within its cap,
// and one that cares about nothing in the memory to the memory whose sockets are worth least to the others (worth: its
// hits, casts and damage, each over the most among the worn memories, averaged).
// Each essence's values are divided by its best among the worn memories (rel, 0..1), so that an option's worth is the
// sum over the essences it makes fire of how close each is to its own best. keepCmp: the number made to fire first
// (pairing still wins when more fire), then the moved one live, then this worth (differences under VALUE_TIE are a
// tie), then the old order.
const MEM_PRIOR = {   // St_ left out of the keys: [cd, casts a cooldown, hits a cast on one enemy, enemies a hit, damage a hit (AP/AD), proc coefficient, hits a minute without a cast]
  C_BackStep:[8.5,1,3,1.5,1.68,0.5], C_BeamOfLight:[10,1,10,1,0.86,0.25], C_CorrosiveTrails:[10,1,1,1,1,1],
  C_DarkBolt:[4.5,1,1,1.5,4.4,1], C_DarkSpear:[5,1,1,1.5,4.6,1], C_FlashFreeze:[9,1,1,2,1.8,1],
  C_GlacialStomp:[8,1,1,1,2.1,1], C_Hemorrhage:[4.5,1,5,1,0.92,0.48], C_IceBlock:[6,1,0.5,2,1.6,1],
  C_IceClaw:[7,1,2,1.5,1.85,1], C_MagicSword:[5.5,1,1,1.5,2,1], C_MassProtection:[7,1,0,0,0,1],
  C_Pew:[10,2.5,1,1.3,2,1], C_PressurePoint:[5.5,1,1,2,1.8,1], C_Purgatory:[11,1,1,2,2.8,1],
  C_Sneeze:[2,1,0,0,0,1], C_SparklingWaterGun:[4,1,1,2,1,1], C_Starfall:[8,1,1,1,0.7,1],
  C_SwiftSlash:[7,1,1,1,2.2,1], C_Whirlwind:[10,1,1,2,1,1], E_AntiGravity:[14,1,1,2,4,1],
  E_Blink:[5.5,1,1,2,2.44,1], E_ChainLightning:[16,1,1,4,2.3,0.8], E_ClutchesOfMalice:[11,1,1,2,2,1],
  E_CrimsonLance:[16,1,1,2,4.5,1], E_DoomsdayMeteor:[100,1,1,2,16,1], E_FinalExplosion:[3,1,1,1,8.5,1],
  E_FlameJet:[11.5,1,1,1,1,1], E_Harvest:[12.5,1,1,1,1,1], E_JusticeGuillotine:[100,1,1,2,12,1],
  E_LizardlyBlessing:[8,1,1,1,1.25,1], E_MassCleanse:[10,1,1,2,3,1], E_MysticDagger:[6,1,1,1,3.5,1],
  E_Permafrost:[100,1,1,2,5,1], E_Rewind:[12,1,1,1,1.6,1], E_SearingCharge:[9,1,2,1.5,1.5,1],
  E_ShadowVolley:[10,1,1,1,0.6,1], E_SliceThroat:[7,1,1,2,2.5,1], E_StygianRush:[11,1,1,2,4,1],
  E_UmbralEdge:[10,1,1,1,0.3,1], E_VileStrike:[6,1,1,1,2,1], E_WinterDive:[12,1,1,2,3.4,1],
  L_Blizzard:[14.5,1,1,2,1.8,1], L_ButchersStrike:[8,1,1,2,2,1], L_CoinExplosion:[5,1,1,2,1.5,1],
  L_LightExplosion:[13,1,1,2,4,1], L_MentalCorruption:[16,1,1,1,2,1], L_Multishot:[11,1,1,1,0.35,1],
  L_PyranasFireball:[11,1,1,1,2.2,1], L_SpectreBullet:[6,1,1,1,3,1], Q_CruelSun:[6.5,1,1,2,3.8,1],
  Q_Discipline:[7,1,1,2,1.75,1], Q_EtherealInfluence:[9.5,1,1,2,1.8,1], Q_Fleche:[12,1,1,1,1.4,1],
  Q_GoldenBurst:[0.25,1,1,2,1.1,1], Q_HandCannon:[5.5,1,1,2,1,1], Q_IncendiaryRounds:[7.5,1,4,1,1,1],
  Q_Laceration:[7.5,1,1,2,2.3,1], Q_Lunge:[6,1,1,2,2,1], Q_MoonlightPact:[11,1,1,1,2,1],
  Q_Reduction:[4,1,1,2,0.95,1], Q_SuperNova:[8,1,1,2,2,1], Q_SylvanCall:[9,1,1,2,2,1],
  QR_DistortedMind:[8,1,1,2,1.7,1], QR_InfernalTales:[5,1,1,1,1,1], QR_Innocence:[7,1,1,2,1.08,1],
  QR_ValiantHeart:[4.5,1,1,1,2.4,1], R_AnnihilationStance:[100,1,1,2,4,1], R_BaptismOfSun:[7,1,1,2,2,1],
  R_BlackArbalest:[11,1,1,1,2.5,1], R_BoneCrusher:[12,1,2,1.5,2.8,1], R_Cataclysm:[80,1,1,1,2.5,1],
  R_ChainReaction:[100,1,1,1,1,1], R_Chomp:[6,1,1,1,3.7,1], R_DancingBlades:[13,1,1,2,0.85,1],
  R_DangerousTheory:[7.5,1,1,1,1.25,1], R_DarkGrenade:[9,1,1,1,1.5,1], R_FlamingWhip:[4.5,1,1,2,2,1],
  R_Frostbite:[6.5,1,2,1,1.15,1], R_GlacialHammer:[9,1,1,2,2.5,1], R_GreatFrostSword:[9,1,1,1.5,4,1],
  R_Ignite:[10,3,1,1.3,1.3,1], R_Immolation:[9,1,1,2,1,1], R_Inspire:[7,1,1,1,0.13,1],
  R_LightningDance:[13,1,1,2,1.2,1], R_NaturesWhisper:[8,1,0,0,0,1], R_OrbOfLight:[8,1,1,2,2,1],
  R_Parry:[10,1,1,1,3.6,1], R_PhaseShift:[9,1,1,2,2,1], R_PillarOfFlame:[11,1,1,2,2.75,1],
  R_PrecisionShot:[10,1,1,1.5,5,1], R_QuickTrigger:[14,1,1,1,1.2,1], R_RepulsiveShield:[9,1,2,2,2.4,0.5],
  R_SanctuaryOfEl:[120,1,1,2,5,1], R_Scattershot:[8,1,4,1.5,1,1], R_SerpentineBlessing:[80,1,1,1,1,1],
  R_ShadowOverdrive:[10,1,1,2,0.5,1], R_ShadowWalk:[7.5,1,1,2,0.65,1], R_Smite:[13,1,1,2,4,1],
  R_Somersault:[10,1,0,0,0,1], R_StaticDischarge:[8.5,1,1,2,7.8,1], R_SummonLittleBaam:[14,1,1,2,0.85,1],
  R_Tranquility:[12,1,0,0,0,1], R_UnbreakableDetermination:[70,1,1,2,4,1], U_BeamOfBalance:[100,1,1,2,4.6,1],
  U_Burrow:[12,1,1,2,5.5,1], U_HerWorld:[100,1,1,2,12,1], U_Hysteria:[15,1,1,2,1,1],
  U_ShoutOfOblivion:[100,1,1,2,13,1], U_WorldCracker:[13,1,1,2,1,1], L_HerosReturn:[150,1,1,1,1,1],
  L_SmallMoltenCore:[10,1,1,1.5,0.8,1,60], Q_BigBorealChunk:[10,1,1,2,1.2,1], Q_DeathMark:[12,1,1,2,3.4,1],
  Q_EmbracingTheChill:[10,1,1,2,2,1], R_AnnoyingBanner:[12,1,1,2,1,1], R_BackOff:[7,1,1,1,4.5,1],
  R_Deception:[10,1,1,2,3.5,1], R_FrozenFists:[13,1,1,1,1.7,1], U_BigChomp:[8,1,1,2,3.7,1],
};
const ESS_KIND = {   // Gem_ left out of the keys
  C_Charcoal: 'hp', C_Confidence: 'd', C_Efficiency: 'd', C_Guidance: 'l', C_Lethality: 'd', C_Love: 'l', C_Quicksilver: 'c',
  C_Regeneration: 'c', C_Responsibility: 'e8', C_Sharp: 'c', C_Shatter: 'e8s', C_Sulfur: 'd', C_Talc: 'e8s', C_Vengeance: 'd',
  C_Void: 'c', C_Wind: 'c', C_CamillasGiftRuined: 'o',
  E_Aftershock: 'c', E_Apathy: 'h', E_Blossom: 'd', E_Clemency: 'd', E_Crimson: 'o', E_Direness: 'd', E_Domination: 'o',
  E_Fangs: 'd', E_Fever: 'h', E_Flexibility: 'o', E_Insensitivity: 'c', E_Insight: 'h3', E_Inversion: 'd', E_Metal: 'd',
  E_Might: 'd', E_Obsidian: 'c', E_Omega: 'd', E_Opportunity: 'c8', E_OurStory_Completed: 'o', E_OurStory_Unfinished: 'o',
  E_Overload: 'd', E_Pain: 'd', E_Predation: 'd', E_Protection: 'c5', E_Reflex: 'o', E_Thunder: 'c', E_Twilight: 'h',
  E_Umbra: 'd', E_Virtuousness: 'd',
  L_CamillasGift: 'o', L_ChaosApple: 'o', L_Culinary: 'h', L_DivineFaith: 'd', L_Embertail: 'd', L_HeartOfGold: 'd',
  L_Infinity: 'o', L_Liberty: 'c', L_MetalCrystal: 'o', L_Paranoia: 'd', L_Perfect: 'o', L_PureWhite: 'e10s', L_SolarEye: 'e20',
  L_Supersymmetry: 'o', L_SuppressedArcanum: 'c',
  R_Abyss: 'hp', R_Accuracy: 'hs', R_Adventure: 'o', R_Blade: 'h', R_Bleak: 'd', R_Blood: 'o', R_Celestial: 'e8', R_Composure: 'c',
  R_Contempt: 'd', R_Control: 'h', R_Crucible: 'd', R_Dusk: 'c', R_Epiphany: 'c10', R_Flow: 'd', R_Frost: 'h', R_Glaciate: 'c',
  R_Glass: 'l', R_Hedgehog: 'c', R_Insatiable: 'c', R_Lava: 'e4', R_Lightweight: 'd', R_Momentum: 'd', R_Mortality: 'hp',
  R_NightSky: 'hp', R_Panic: 'd', R_Purity: 'c4', R_Rejuvenation: 'e15s', R_Ricochet: 'd', R_Rigidity: 'hp', R_Scorched: 'c',
  R_Shock: 'o', R_Slippery: 'd', R_Snow: 'o', R_Spiral: 'o', R_Stillness: 'e5', R_Wealth: 'h', R_Wound: 'h',
  U_EternalFlame: 'd', U_GlacialCore: 'h5', U_LastStarlight: 'e15', U_SoulPrison: 'o', U_GuidingCompass_Charged: 'o',
  U_GuidingCompass_NotCharged: 'o',
};
const VALUE_TIE = 0.1;
const VALUE_SWAP = 0.5;   // planHeld: in place of a Dead one rather than into a free socket, as many firing, worth this more
// An option's worth: over the essences it makes fire (the held one if live there, the Dead ones it wakes), how close
// each is to its own best memory. null when none of them can be valued.
function optValue(o, rv, slots, use) {
  const parts = [];
  if (o.live) parts.push(rv ? rv.rel.get(o.s.slot) ?? 0 : null);
  for (const w of o.woken || []) {
    const r = relValues(w.type, w.profile, null, slots, use, () => false, w.quality);
    parts.push(r ? r.rel.get(o.s.slot) ?? 0 : null);
  }
  const known = parts.filter(x => x != null);
  return known.length ? known.reduce((a, b) => a + b, 0) : null;
}
const KIND_SAY = { h: 'on hit', c: 'on cast', e: 'an empowered cast', d: 'with the damage', l: 'on heals', o: 'any memory' };
// An essence's kind: { on, cd, s, p } - from ESS_KIND, its own limits when DevTools sends them (fit.gem.limits: the
// cooldown and rate limit its prefab and quality give it), else from its profile (an essence ESS_KIND does not know).
function essKind(type, prof, limits) {
  const code = ESS_KIND[String(type || '').replace(/^Gem_/, '')];
  const m = /^([hcedlo])(\d+(?:\.\d+)?)?(s?)(p?)$/.exec(code || '');
  let k;
  if (m) k = { on: m[1], cd: m[2] ? +m[2] : 0, s: !!m[3], p: !!m[4] };
  else if (prof && !prof.error && Array.isArray(prof.needs)) {
    const n = prof.needs;
    k = { on: n.includes('Damage') ? 'h' : n.includes('Heal') || n.includes('Shield') ? 'l' : n.includes('Cast') ? 'c' : 'o', cd: 0, s: false, p: false };
  } else return null;
  if (limits && !limits.error) {
    if (limits.cooldown > 0) k.cd = limits.cooldown;
    if (limits.rateCount > 0 && limits.rateSeconds > 0) k.cd = Math.max(k.cd, limits.rateSeconds / limits.rateCount);
  }
  return k;
}
// What one essence of kind k gets a minute from a memory used as u.
function essValue(k, u) {
  if (!k || !u) return null;
  const cap = k.cd > 0 ? 60 / k.cd : Infinity;
  switch (k.on) {
    case 'h': return Math.min(k.p ? u.p : u.h, cap) * (k.s ? u.dph : 1);
    case 'c': return Math.min(u.c, cap);
    case 'e': return Math.min(u.c, cap) * (k.s ? u.dpc : 1);
    case 'd': return u.D;
    case 'l': return u.c;
    default: return 1;   // nothing about the memory: the same everywhere (keepCmp then takes the socket worth least)
  }
}
// The bot's own casts in fights, by memory type, and the fight milliseconds each was worn (fight() feeds it).
// Iteration 37: lx - the level multiplier (MEM_LVL) x those milliseconds: the seen damage at level 1 for the log (the choice
// itself takes only rates from what was seen - lvlAvgOf, memStats).
const castTally = { n: {}, ms: {}, lx: {}, at: 0 };
function tallyTick(hero, fighting) {
  const now = Date.now(), dt = castTally.at ? Math.min(now - castTally.at, 1000) : 0;
  castTally.at = now;
  if (!fighting || !hero) return;
  for (const k of hero.skills || []) if (k.type && ['Q', 'W', 'E', 'R'].includes(k.slot)) {
    castTally.ms[k.type] = (castTally.ms[k.type] || 0) + dt;
    castTally.lx[k.type] = (castTally.lx[k.type] || 0) + lvlX((MEM_LVL[memKey(k.type)] || MEM_LVL_DEF)[0], k.level) * dt;
  }
}
const tallyCast = type => { castTally.n[type] = (castTally.n[type] || 0) + 1; };
// Each worn memory's use a minute: { c casts, h hits, p proc-weighted hits, D damage, net (D less the channel's cost), dph,
// dpc, worth (hits, casts and damage each over the most among them, averaged: what its sockets are worth to essences in
// general), src, row, hero }.
// Iteration 40: the SAME fair reference as the upgrades' (memStats - iteration 37): the rates seen blended with the dump's
// prior by the sample, the bought haste taken out, the damage a hit by the formula at level 1 with no essence x what lands
// of it (the landing factor). Until now it was DevTools' damage seen (levels and essences bought in it: run-049's R at
// 1619 against Q's 851 after two levels) once every memory had 3 casts, else the live cooldowns (bought haste in) x
// MEM_PRIOR's AP multiples - "32d" meant 32 AP, not damage. o: { tally, chan, ges, lvlAvg } - the run's by default (a bare
// tally is taken as o.tally, as iteration 29 passed it).
function memUse(slots, hero, o = {}) {
  const out = new Map();
  if (!slots || !slots.length) return out;
  if (o && o.n && o.ms) o = { tally: o };
  const tally = o.tally || castTally;
  const ctx = { chan: o.chan || chanOf(), tally, ges: o.ges || (typeof upGes !== 'undefined' ? upGes : {}), lvlAvg: o.lvlAvg || lvlAvgOf(tally) };
  const skills = slots.filter(s => s.memory).map(s => {
    const k = ((hero && hero.skills) || []).find(x => x.slot === s.slot && x.type === s.memory) || {};
    return { ...k, slot: s.slot, type: s.memory, level: k.level || s.level || 1, rarity: k.rarity || s.rarity,
      trigger: k.trigger || (s.cooldown > 0 ? { maxCooldown: s.cooldown } : null), gems: k.gems || s.gems || [] };
  });
  const rows = memStats({ ...(hero || {}), skills }, slots.map(s => ({ memory: s.memory, use: s.use })), ctx);
  for (const r of rows) {
    const u = { c: r.c, h: r.h, p: r.p, D: r.D, net: Math.max(0, r.D - r.chanCost), src: r.src, row: r, hero };
    u.dph = u.h > 0 ? u.D / u.h : 0;
    u.dpc = u.c > 0 ? u.D / u.c : 0;
    out.set(r.slot, u);
  }
  const top = k => Math.max(0, ...[...out.values()].map(u => u[k]));
  const tD = top('D'), tH = top('h'), tC = top('c');
  for (const u of out.values()) u.worth = ((tD > 0 ? u.D / tD : 0) + (tH > 0 ? u.h / tH : 0) + (tC > 0 ? u.c / tC : 0)) / 3;
  return out;
}
// What an essence gives a minute in each worn memory (raw) and over its best (rel). Iteration 40: by the upgrades' own
// measure where GEM_EFF values it (gemDpm on the memory's fair row - damage a minute, comparable across essences: dpm
// true), else by its trigger (ESS_KIND: casts, hits... on the same fair rows - comparable only across memories). null
// when it cannot be told. `dead`: the slots where it cannot fire (its verdicts) - 0 there, and not its best. q: its quality.
// No diminishing returns to model: the game multiplies amps (DamageData.ApplyAmplification), so a second amp in a memory
// adds its share of that memory's damage as the first did (a little more in absolute terms - not counted, so that the
// reference stays essence-free).
function relValues(type, prof, limits, slots, use, dead = () => false, q = 100) {
  if (!use || !use.size) return null;
  const k = essKind(type, prof, limits), e = GEM_EFF[gemKey(type)];
  let v = null, dpm = false;
  if (e && e[0] !== 'O') {
    const m = new Map(slots.map(s => { const u = use.get(s.slot); return [s.slot, dead(s) || !u ? 0 : gemDpm(type, q ?? 100, u.row, u.hero)]; }));
    if (Math.max(0, ...m.values()) > 0) { v = m; dpm = true; }
  }
  if (!v) {
    if (!k) return null;
    v = new Map(slots.map(s => [s.slot, dead(s) ? 0 : essValue(k, use.get(s.slot))]));
  }
  const top = Math.max(0, ...[...v.values()].filter(x => x != null));
  if (!(top > 0)) return null;
  return { kind: k, form: dpm ? e[0] : null, dpm, raw: v, rel: new Map([...v].map(([sl, x]) => [sl, x == null ? 0 : x / top])) };
}
const r2 = x => x >= 100 ? String(Math.round(x)) : x >= 10 ? x.toFixed(1) : x.toFixed(2);
const kindTag = k => k ? `${KIND_SAY[k.on]}${k.p ? ' (by proc)' : ''}${k.s ? ' x its damage' : ''}${k.cd ? `, ${k.cd} s cd` : ''}` : 'kind unknown';
const FORM_SAY = { H: 'per hit', P: 'per hit by proc', M: 'per hit, max hp', C: 'per cast', E: 'an empowered cast', X: 'a share of the cast', A: 'an amp',
  S: 'memory haste', T: 'cooldown cut per attack', W: 'attack speed', K: 'per basic attack', LC: 'heals per cast', LA: 'heals by damage', LH: 'heals per hit' };
// For the log: the held essence's value in each memory (over its best; its damage a minute there when GEM_EFF values it),
// and what the memories do a minute on the fair reference (level 1, no essence; the damage what lands).
function valueLine(rv, use, slots) {
  if (!rv) return 'value: not known';
  const per = slots.map(s => `${s.slot} ${(rv.rel.get(s.slot) || 0).toFixed(2)}${rv.dpm ? ` (${Math.round(rv.raw.get(s.slot) || 0)})` : ''}`).join(' ');
  const mem = slots.map(s => { const u = use.get(s.slot); return u ? `${s.slot} ${String(s.memory).replace(/^St_/, '')} ${r2(u.c)}c ${r2(u.h)}h ${r2(u.D)}d${u.row && u.row.landSeen != null ? ` x${u.row.land.toFixed(2)}` : ''}${u.row && u.row.chanCost ? ` -${Math.round(u.row.chanCost)}ch` : ''} (${u.src})` : s.slot; }).join(', ');
  return `value (${rv.dpm ? `${FORM_SAY[rv.form] || rv.form}, dpm` : kindTag(rv.kind)}): ${per} | a minute (fair, lvl 1): ${mem}`;
}
// --- end essence value ---
// ----- iteration 37: impact and upgrades -------------------------------------------------------------------------------
// The user: "the bot always upgrades Precision Shot as the most important skill, while in almost every run it is, if not the
// most useless skill, at least not the best candidate"; then "essences can be an order of magnitude more important than
// skills". Until now the boss soul took R, then the rarest; the wells R, W, E, Q in that order while the dust lasted; a new
// memory replaced W/E by rarity. Runs 043-046 (/hero/use): R dealt 28-36% of the memories' damage and got 38 of the 41
// upgrades; in run-046 Dark Bolt (E, level 1 all run) dealt 46%, R (levels 1 -> 14) 28%.
// Now every choice goes by one measure - what a memory (or an essence) adds a minute in fights - measured fairly (the
// user: "invested skills will always look more valuable than new ones that have not built up statistics yet"):
// - From what was seen only the RATES are taken - uses a combat minute (/hero/use, else the bot's own casts in fights,
//   castTally), hits a use, enemies a use, the proc share - with the bought part taken out of the rate where the cooldown
//   binds (the levels' skill haste, the essences' memory haste), and each blended with the dump's prior by the sample:
//   (prior x K + seen x n) / (K + n), K = 60 combat seconds (a rate) or 5 casts. A memory just put on starts at its prior.
//   The overkill holds, the immunity and danger holds and the reach are in the seen rate: a skill held more than its
//   cooldown binds (bound: casts seen / casts its cooldown and charges allow; half of that for one whose cooldown events
//   cut or reset - MEM_LVL ev) gets little from a shorter cooldown.
// - The damage a use is the formula's - MEM_PRIOR's AP/AD multiple a hit x the hero's AP or AD (MEM_LVL says which; for
//   Precision Shot by the charge the bot gives it) - at level 1 with no essence: never the damage seen, which has the
//   levels and essences bought in it. So memories compare at equal investment; a memory's own level counts where it is
//   real - what a swap loses (the levels live on the memory) and the price of the next one.
// - Its channel costs time: the seconds each cast holds the hero (chanTally: Precision Shot's charge as sent + its cast
//   daze, another charged skill's charge, a channel's lock) x uses a minute x the basic attacks' damage a second, and
//   RISK_W of that again for the dodges and steps it rules out - taken off its damage. Plus heals/barriers a minute x
//   SUSTAIN_W, and x (1 + CC_W) for a HardCC memory - the impact.
// - What +1 level adds (the game: ScalingValue - a SkillDefault value x (1 + 0.25 (level - 1)); SkillTrigger - skill haste
//   gainedSkillHastePerSkillLevel a level, not for an Ultimate, a cooldown floor): the damage at level 1 x its per-level
//   multiplier (MEM_LVL, from memories.json) - the same at every level, the scaling being linear - the heals likewise, and
//   the extra casts the haste gives where the cooldown binds.
// - What +quality adds to an essence (Gem: effectiveLevel = quality + 1, GemDefault 0.01 x its multiplier a quality point;
//   a well adds gemAddedQualityOnUpgrade, the soul Ai_RandomGemUpgrade.addedQuality - 50 each): its damage a minute in its
//   memory (GEM_EFF by form - per hit, per cast, per empowered cast, an amp of the memory's damage, haste - from
//   essences.json, x the memory's reference rates and damage: level 1, no essence, so that essences do not pile into the
//   memory already levelled) x the relative gain. A Dead essence, or one of form O (stats, gold, defence,
//   conditions not modelled), gains nothing.
// The wells spend the dust on the best gain per dust, one click at a time, the costs read from the game
// (GetSkillUpgradeDreamDustCost, GetGemUpgradeDreamDustCost - pure getters); the soul (free) takes the best gain. Each
// choice is logged with every candidate's numbers (the prior, the seen and the blended rate, the channel, the damage at
// level 1). sortHands and the shop replace a W/E memory only for one whose worth at its level is REPLACE_BY more than
// the least worn one's at its own. The tables: history/it37/gen-upgrade.mjs.
const MEM_LVL = {   // St_ left out: [damage x per level, damage stat p=AP d=AD, heal/barrier a cast (AP/AD multiple), its x per level, HardCC, Ultimate, heal stat, cooldown cut by events]
  C_BackStep:[0.25,"p",0,0,1,0,"p",0], C_BeamOfLight:[0.25,"p",0,0,0,0,"p",0],
  C_CorrosiveTrails:[0.25,"p",0,0,0,0,"p",0], C_DarkBolt:[0.25,"d",0,0,0,0,"d",1],
  C_DarkSpear:[0.25,"d",0.2,0.125,1,0,"d",0], C_FlashFreeze:[0.25,"p",0.9,0.1875,1,0,"p",0],
  C_GlacialStomp:[0.25,"p",0.4,0.1875,1,0,"p",0], C_Hemorrhage:[0.25,"p",0,0,0,0,"p",0],
  C_IceBlock:[0.25,"p",1.3,0.1875,1,0,"p",0], C_IceClaw:[0.25,"p",0,0,1,0,"p",0],
  C_MagicSword:[0.25,"p",0,0,0,0,"p",0], C_MassProtection:[0,"p",2.5,0.1625,0,0,"p",0],
  C_Pew:[0.25,"p",0,0,0,0,"p",1], C_PressurePoint:[0.25,"p",0,0,1,0,"p",0],
  C_Purgatory:[0.25,"p",0.45,0.1875,0,0,"p",0], C_Sneeze:[0,"p",0,0,0,0,"p",0],
  C_SparklingWaterGun:[0.25,"p",0.7,0.25,1,0,"p",0], C_Starfall:[0.1125,"p",0,0,0,0,"p",0],
  C_SwiftSlash:[0.25,"d",0,0,0,0,"d",1], C_Whirlwind:[0.25,"p",0,0,0,0,"p",0],
  E_AntiGravity:[0.25,"p",0,0,1,0,"p",0], E_Blink:[0.25,"p",0,0,0,0,"p",1],
  E_ChainLightning:[0.25,"p",0,0,0,0,"p",0], E_ClutchesOfMalice:[0.25,"p",0,0,1,0,"p",0],
  E_CrimsonLance:[0.25,"d",0,0,0,0,"d",0], E_DoomsdayMeteor:[0.25,"p",0,0,1,1,"p",0],
  E_FinalExplosion:[0.25,"p",0,0,1,0,"p",0], E_FlameJet:[0.25,"p",0,0,0,0,"p",0],
  E_Harvest:[0.25,"d",0.2,0.1875,0,0,"d",0], E_JusticeGuillotine:[0.25,"d",0,0,0,1,"d",0],
  E_LizardlyBlessing:[0.25,"p",0,0,0,0,"p",0], E_MassCleanse:[0.25,"p",0.8,0.25,0,0,"p",0],
  E_MysticDagger:[0.1875,"p",0,0,0,0,"p",1], E_Permafrost:[0.25,"p",3,0.25,1,1,"p",0],
  E_Rewind:[0.25,"p",0,0,0,0,"p",1], E_SearingCharge:[0.25,"p",0,0,0,0,"p",1],
  E_ShadowVolley:[0.25,"d",0,0,0,0,"d",0], E_SliceThroat:[0.25,"d",0,0,0,0,"d",1],
  E_StygianRush:[0.25,"d",0.6,0.1875,1,0,"d",0], E_UmbralEdge:[0.15,"d",0,0,0,0,"d",0],
  E_VileStrike:[0.25,"d",0,0,1,0,"d",0], E_WinterDive:[0.25,"p",0,0,1,0,"p",0],
  L_Blizzard:[0.25,"p",0.6,0.1875,0,0,"p",0], L_ButchersStrike:[0.25,"d",0.25,0.1875,0,0,"p",0],
  L_CoinExplosion:[0.25,"p",0,0,1,0,"p",0], L_LightExplosion:[0.25,"p",0,0,1,0,"p",0],
  L_MentalCorruption:[0.25,"p",0,0,0,0,"p",0], L_Multishot:[0.25,"p",0,0,0,0,"p",0],
  L_PyranasFireball:[0.25,"p",0,0,0,0,"p",0], L_SpectreBullet:[0.25,"d",0,0,0,0,"d",0],
  Q_CruelSun:[0.25,"p",0,0,1,0,"p",0], Q_Discipline:[0.25,"d",0,0,0,0,"d",1],
  Q_EtherealInfluence:[0.25,"p",0,0,1,0,"p",0], Q_Fleche:[0.25,"d",0,0,0,0,"d",1],
  Q_GoldenBurst:[0.2875,"p",0.275,0.25,0,0,"p",0], Q_HandCannon:[0.25,"p",0,0,1,0,"p",0],
  Q_IncendiaryRounds:[0.25,"p",0,0,0,0,"p",0], Q_Laceration:[0.25,"d",0,0,0,0,"d",1],
  Q_Lunge:[0.25,"d",0,0,0,0,"d",1], Q_MoonlightPact:[0.35,"p",0,0,1,0,"p",0],
  Q_Reduction:[0.25,"p",0.18,0.1875,0,0,"p",0], Q_SuperNova:[0.25,"p",0,0,0,0,"p",0],
  Q_SylvanCall:[0.25,"p",0,0,1,0,"p",0], QR_DistortedMind:[0.25,"d",0,0,1,0,"d",0],
  QR_InfernalTales:[0.25,"p",0,0,0,0,"p",0], QR_Innocence:[0.1,"p",0,0,0,0,"p",0],
  QR_ValiantHeart:[0.25,"d",0,0,1,0,"d",0], R_AnnihilationStance:[0.25,"p",0,0,0,1,"p",0],
  R_BaptismOfSun:[0.25,"p",0,0,0,0,"p",0], R_BlackArbalest:[0.25,"d",0,0,1,0,"d",0],
  R_BoneCrusher:[0.25,"p",0,0,1,0,"p",0], R_Cataclysm:[0.25,"p",1.5,0.25,1,1,"p",0],
  R_ChainReaction:[0.25,"p",0,0,0,1,"p",0], R_Chomp:[0.25,"p",0.75,0.1875,0,0,"p",0],
  R_DancingBlades:[0.25,"p",0,0,0,0,"p",0], R_DangerousTheory:[0.25,"d",0.67,0.1875,0,0,"p",1],
  R_DarkGrenade:[0.25,"d",0,0,0,0,"d",0], R_FlamingWhip:[0.25,"p",0,0,0,0,"p",1],
  R_Frostbite:[0.25,"p",1.1,0.1875,0,0,"p",0], R_GlacialHammer:[0.25,"p",0,0,1,0,"p",0],
  R_GreatFrostSword:[0.25,"p",0,0,1,0,"p",0], R_Ignite:[0.25,"p",0,0,0,0,"p",0],
  R_Immolation:[0.25,"p",0,0,0,0,"p",0], R_Inspire:[0.25,"p",0,0,0,0,"p",0],
  R_LightningDance:[0.25,"p",0,0,0,0,"p",1], R_NaturesWhisper:[0,"p",0,0,0,0,"p",0],
  R_OrbOfLight:[0.25,"p",0.25,0.1875,0,0,"p",0], R_Parry:[0.125,"p",0,0,1,0,"p",1],
  R_PhaseShift:[0.25,"p",0,0,1,0,"p",0], R_PillarOfFlame:[0.25,"p",0,0,0,0,"p",0],
  R_PrecisionShot:[0.25,"p",0,0,1,0,"p",0], R_QuickTrigger:[0.25,"d",0,0,0,0,"d",0],
  R_RepulsiveShield:[0.25,"p",0,0,0,0,"p",0], R_SanctuaryOfEl:[0.25,"p",2,0.375,0,1,"p",0],
  R_Scattershot:[0.25,"d",0,0,0,0,"d",0], R_SerpentineBlessing:[0.25,"p",0,0,1,1,"p",0],
  R_ShadowOverdrive:[0.25,"d",0,0,0,0,"d",0], R_ShadowWalk:[0.25,"d",0,0,0,0,"d",0],
  R_Smite:[0.25,"p",0,0,1,0,"p",0], R_Somersault:[0,"p",0,0,1,0,"p",1], R_StaticDischarge:[0.25,"p",0,0,1,0,"p",0],
  R_SummonLittleBaam:[0.25,"p",0,0,0,0,"p",0], R_Tranquility:[0,"p",0,0,0,0,"p",1],
  R_UnbreakableDetermination:[0.25,"p",0,0,0,1,"p",1], U_BeamOfBalance:[0.25,"p",2,0.1875,0,1,"p",0],
  U_Burrow:[0.25,"p",0,0,1,0,"p",0], U_HerWorld:[0.25,"p",0,0,1,1,"p",0],
  U_Hysteria:[0.25,"d",0.125,0.0875,1,0,"d",0], U_ShoutOfOblivion:[0.25,"d",0,0,1,1,"d",0],
  U_WorldCracker:[0.25,"p",0,0,0,0,"p",0], L_HerosReturn:[0.25,"p",8,0.125,0,1,"p",0],
  L_SmallMoltenCore:[0.25,"p",0,0,0,0,"p",0], Q_BigBorealChunk:[0.25,"p",0,0,0,0,"p",0],
  Q_DeathMark:[0.25,"p",0,0,0,0,"p",1], Q_EmbracingTheChill:[0.25,"p",0.25,0.125,1,0,"p",0],
  R_AnnoyingBanner:[0.25,"p",0,0,0,0,"p",0], R_BackOff:[0.25,"d",1.35,0.125,1,0,"p",0],
  R_Deception:[0.25,"d",0,0,0,0,"d",0], R_FrozenFists:[0.25,"d",0.6,0.125,1,0,"d",0],
  U_BigChomp:[0.25,"d",0.3,0.1875,0,0,"d",1],
};
const GEM_EFF = {   // Gem_ left out: [form, const, AP, AD, x per quality, k, cap, x enemies a cast] (history/it37/gen-upgrade.mjs)
  C_CamillasGiftRuined:["O"], C_Charcoal:["P",0,0.3,0,0.01,1.3,0,0], C_Confidence:["A",0.2,0,0,0.01,0.7,0,0],
  C_Efficiency:["S",18,0,0,0.01,1,0,0], C_Guidance:["O"], C_Lethality:["A",0.3,0,0,0.01,0.4,0,0], C_Love:["O"],
  C_Quicksilver:["O"], C_Regeneration:["LC",25.5,0,0,0.01,1,0,0], C_Responsibility:["E",0,0,2,0.01,1,8,0],
  C_Sharp:["C",0,0.45,0,0.01,5,0,0], C_Shatter:["X",0.2,0,0,0.01,1,8,0], C_Sulfur:["A",0.075,0,0,0.01,1,0,0],
  C_Talc:["E",0,2,0,0.01,1,8,1], C_Vengeance:["O"], C_Void:["C",0,0,0.75,0.01,1.5,0,0],
  C_Wind:["W",22,0,0,0.005,1,5,0], E_Aftershock:["C",0,1,0,0.01,1.5,0,0], E_Apathy:["O"], E_Blossom:["O"],
  E_Clemency:["LA",0.03,0,0,0.005,1,0,0], E_Crimson:["K",0,0,0.5,0.01,0.375,0,0], E_Direness:["O"],
  E_Domination:["O"], E_Fangs:["K",0,0,0.3,0.01,0.5,0,0], E_Fever:["H",0,0.65,0,0.01,1.5,30,0],
  E_Flexibility:["O"], E_Insensitivity:["C",0,0.7,0,0.01,2,0,0], E_Insight:["H",0,1,0,0.01,2.5,20,0],
  E_Inversion:["A",0.1,0,0,0.01,0.8,0,0], E_Metal:["O"], E_Might:["O"], E_Obsidian:["C",0,0,1.25,0.01,1,0,0],
  E_Omega:["O"], E_Opportunity:["O"], E_OurStory_Completed:["O"], E_OurStory_Unfinished:["O"],
  E_Overload:["A",0.225,0,0,0.01,1,0,0], E_Pain:["A",0.2,0,0,0.01,1,0,0], E_Predation:["A",0.2,0,0,0.01,0.3,0,0],
  E_Protection:["O"], E_Reflex:["O"], E_Thunder:["C",0,0.65,0,0.008,3,0,0], E_Twilight:["H",0,0.27,0,0.01,3,12,0],
  E_Umbra:["O"], E_Virtuousness:["O"], L_CamillasGift:["O"], L_ChaosApple:["O"],
  L_DivineFaith:["A",30,0,0,0.015,0.004,0,0], L_Embertail:["K",0,0.5,0,0.01,1,0,0],
  L_HeartOfGold:["A",0.06,0,0,0.01,3,0,0], L_MetalCrystal:["O"], L_Paranoia:["S",75,0,0,0.01,0.4,0,0],
  L_Perfect:["O"], L_PureWhite:["X",0.1,0,0,0.01,3,10,0], L_SolarEye:["O"],
  L_SuppressedArcanum:["C",0,1.2,0,0.01,1.5,0,0], R_Abyss:["O"], R_Accuracy:["A",0.25,0,0,0.01,0.5,0,0],
  R_Adventure:["O"], R_Blade:["H",0,0,0.66,0.0005,2,30,0], R_Bleak:["A",0.25,0,0,0.01,0.4,0,0], R_Blood:["O"],
  R_Celestial:["E",0,0.65,0,0.01,4,8,0], R_Composure:["O"], R_Contempt:["A",0.25,0,0,0.01,0.5,0,0],
  R_Dusk:["C",0,0,0.15,0.01,10,0,0], R_Epiphany:["O"], R_Flow:["O"], R_Frost:["M",0.07,0,0,0.004,1,12,0],
  R_Glaciate:["C",0,0.8,0,0.01,1.5,0,0], R_Glass:["O"], R_Insatiable:["W",10,0,0,0.005,1,5,0], R_Lava:["O"],
  R_Momentum:["T",0.2,0,0,0.012,0.6,0,0], R_Mortality:["O"], R_NightSky:["W",10,0,0,0.01,1,3,0], R_Panic:["O"],
  R_Purity:["O"], R_Rejuvenation:["LA",0.12,0,0,0.002,0.1,0,0], R_Ricochet:["A",0.6,0,0,0.005,0.5,0,0],
  R_Rigidity:["LH",5,0,0,0.0075,1,0,0], R_Scorched:["C",0,0.35,0,0.0035,3,0,0], R_Shock:["K",0,0,0.2,0.01,0.5,0,0],
  R_Snow:["O"], R_Spiral:["O"], R_Stillness:["E",0,1,0,0.01,1.5,5,0], R_Wealth:["O"],
  R_Wound:["H",0,1.1,0,0.015,1.7,20,0], U_EternalFlame:["A",0.01,0,0,0.01,5,0,0], U_GlacialCore:["O"],
  U_LastStarlight:["O"], U_SoulPrison:["O"], L_Culinary:["O"], L_Liberty:["O"], L_Supersymmetry:["O"],
  R_Control:["O"], R_Crucible:["A",0.2,0,0,0.01,0.5,0,0], R_Hedgehog:["O"],
  R_Lightweight:["S",27,0,0,0.01,0.6,0,0], R_Slippery:["A",0.5,0,0,0.01,0.1,0,0], U_GuidingCompass_Charged:["O"],
  U_GuidingCompass_NotCharged:["O"],
};
const IMP = {
  K_SECS: 60, K_CASTS: 5,         // the prior counts as this many combat seconds (a rate) or casts (hits, enemies a cast) of seeing
  SUSTAIN_W: 0.5,                 // a point of heal or barrier against a point of damage
  CC_W: 0.1,                      // a HardCC memory's impact x (1 + CC_W) (keep/replace only: a stun's length does not scale by level)
  REPLACE_BY: 1.25,               // a new memory replaces a worn W/E one only with this much more impact (the prior is rough)
  ATK_UPTIME: 0.6,                // the share of a fight minute the hero is shooting (basic-attack essences, attack speed)
  HASTE_LVL: 4, CD_FLOOR: 0,      // DewGameplayExperienceSettings' defaults; read from the game when possible (upgradeGes)
  GEM_DQ: 50, SOUL_DQ: 50, SOUL_DL: 1,
  RISK_W: 0.5,                    // a second held in a channel costs the basic attacks it rules out and this much again (no dodge, no step)
  EV_BOUND: 0.5,                  // a memory whose cooldown events cut or reset (MEM_LVL ev): half of a shorter cooldown turns into casts
  // Iteration 40: the landing factor - the seen damage a hit (levels and amps out) over the formula's, blended with 1 by
  // this many hits (a per-hit ratio: its sample is the hits); clamped (a prior far off: Dancing Blades' 0.85 AP a hit against ~3.5 seen; overkill: Precision Shot).
  K_LAND: 10, LAND_MIN: 0.25, LAND_MAX: 4,
};
const memKey = t => String(t || '').replace(/^St_/, '');
const gemKey = t => String(t || '').replace(/^Gem_/, '');
const lvlX = (per, L) => 1 + (per || 0) * Math.max(0, (L || 1) - 1);
const statOf = (hero, s) => { const st = (hero && hero.stats) || {}; return (s === 'd' ? st.attackDamage : st.abilityPower) || 0; };
const MEM_LVL_DEF = [0.25, 'p', 0, 0, 0, 0, 'p', 0];
// The hero's attacks a second (its basic attack's cooldown, else the attack speed multiplier).
const heroAps = hero => { const cd = hero && hero.attack && hero.attack.maxCooldown; return cd > 0.15 && cd < 3 ? 1 / cd : ((hero && hero.stats && hero.stats.attackSpeed) || 1); };
// The fight-time average level multiplier per memory type from castTally.lx (tallyTick) - only to show the seen damage at
// level 1 next to the formula's in the log.
function lvlAvgOf(tally) {
  const out = {};
  for (const [t, ms] of Object.entries((tally && tally.ms) || {})) if (ms > 0 && tally.lx && tally.lx[t] > 0) out[t] = tally.lx[t] / ms;
  return out;
}
// The skill haste a level gives (SkillTrigger.GetCooldownMultiplierOfSkillHastePerLevelBonus): the cooldown x cdmOf(L).
const cdmOf = (ges = {}, L = 1, ult = false) => {
  if (ult) return 1;
  const hp = ges.haste ?? IMP.HASTE_LVL, fl = ges.floor ?? IMP.CD_FLOOR;
  return fl + (1 - fl) / (1 + hp * Math.max(0, L - 1) * 0.01);
};
// A damage a hit by the formula (the dump's AP/AD multiple x the hero's stat), at level 1 and with no essence. Precision
// Shot's by the charge the bot gives it (1.5 -> 8 AP over 1 s; chan = the charge + its 0.1 s daze).
function dphOf(key, hero, chan) {
  const pr = MEM_PRIOR[key], ml = MEM_LVL[key] || MEM_LVL_DEF;
  if (!pr) return 0;
  const ap = key === 'R_PrecisionShot' && chan > 0 ? 1.5 + 6.5 * Math.min(1, Math.max(0, chan - 0.1)) : pr[4];
  return ap * statOf(hero, ml[1]);
}
// What the dump says a memory does a minute, as a reference - level 1, no essence (MEM_PRIOR: its cooldown and the casts a
// cooldown gives - charges restored at once, refunds; hits a cast; enemies a hit; damage a hit). cdRef: its cooldown with
// the level's haste and its essences' haste taken out (else the dump's). null: not in the table.
function priorUse(type, hero, cdRef = 0, chan = 0) {
  const key = memKey(type), pr = MEM_PRIOR[key];
  if (!pr) return null;
  const c = 60 / Math.max(cdRef > 0 ? cdRef : pr[0], 0.5) * pr[1];
  const hpu = pr[2] * pr[3], dph = dphOf(key, hero, chan);
  return { c, hpu, tpc: pr[3] || 1, pc: pr[5], passive: pr[6] || 0, dph, D: (c * hpu + (pr[6] || 0)) * dph };
}
// The memory haste its socketed essences give (S form: Efficiency...), to take out of its cooldown.
const gemHaste = (gems, hero) => (gems || []).filter(g => g && g.type && !(g.fit && g.fit.verdict === 'Dead'))
  .reduce((a, g) => { const e = GEM_EFF[gemKey(g.type)]; return a + (e && e[0] === 'S' ? gemSize(e, hero, g.quality ?? 100) : 0); }, 0);
// Iteration 40: what its socketed amps multiply its damage by (A form; the game multiplies them - DamageData.ApplyAmplification:
// amplificationMultiplier *= 1 + value), to take them out of the seen damage a hit.
const gemAmp = (gems, hero) => (gems || []).filter(g => g && g.type && !(g.fit && g.fit.verdict === 'Dead'))
  .reduce((a, g) => { const e = GEM_EFF[gemKey(g.type)]; return a * (e && e[0] === 'A' ? 1 + gemSize(e, hero, g.quality ?? 100) : 1); }, 1);
// Each worn memory (Q W E R), measured fairly: what it would do at level 1 with no essence (the user: "invested skills always
// look more valuable than new ones that have not built up statistics yet"). From what was seen only the RATES are taken -
// casts a combat minute (with the level's and the essences' haste taken out where the cooldown binds), hits a cast,
// enemies a cast, the proc share - each blended with the dump's prior by the sample: (prior x K + seen x n) / (K + n), n
// the combat seconds (IMP.K_SECS) or the casts (IMP.K_CASTS) - a memory just put on starts at its prior. The damage a hit
// is the formula's (dphOf), never the damage seen (that has the levels and the essences in it).
// Row: { slot, type, key, level, ml, prior, seen, c, hpu, h, p, tpc, D (level 1, no essence), DL (at its level), dBase,
// chan, chanCost, sust, impact (level 1), impactL (its level), bound, src, seenD1 }.
// o: { chan: type -> seconds a cast holds the hero (chanOf), tally: the bot's casts in fights (castTally, the rate when
// /hero/use has nothing), ges: the level haste numbers, lvlAvg: for the log }.
function memStats(hero, uses, o = {}) {
  const byType = new Map((uses || []).filter(m => m && m.memory && m.use).map(m => [m.memory, m.use]));
  const basicDps = heroAps(hero) * statOf(hero, 'd'), chanM = o.chan || {}, tally = o.tally || null, lvlAvg = o.lvlAvg || {};
  const rows = [];
  for (const k of (hero && hero.skills) || []) {
    if (!k.type || !['Q', 'W', 'E', 'R'].includes(k.slot)) continue;
    const key = memKey(k.type), ml = MEM_LVL[key] || MEM_LVL_DEF, level = k.level || 1, pr0 = MEM_PRIOR[key];
    const cdLive = k.trigger && k.trigger.maxCooldown > 0 ? k.trigger.maxCooldown : 0;
    const chan = chanM[k.type] > 0 ? chanM[k.type] : 0;
    // Its cooldown with what was bought taken out: the level's haste (cdmOf) and its essences' memory haste.
    const inv = 1 / cdmOf(o.ges, level, !!ml[5]) * (1 + gemHaste(k.gems, hero) / 100);
    const cdRef = cdLive > 0 ? cdLive * inv : 0;
    const prior = priorUse(k.type, hero, cdRef, chan);
    const x = (pr0 || [0, 1])[1] || 1;
    // Seen: /hero/use, else the bot's own casts in fights (a rate only).
    const u = byType.get(k.type);
    let seen = null;
    if (u && u.combatSeconds > 0) seen = { secs: u.combatSeconds, casts: u.casts || 0, hits: u.hits || 0, proc: u.proc ?? u.hits ?? 0, tpc: u.targetsPerCast || 0, damage: u.damage || 0, src: 'seen' };
    else if (tally && tally.n && tally.n[k.type] > 0 && tally.ms[k.type] > 0) seen = { secs: tally.ms[k.type] / 1000, casts: tally.n[k.type], hits: 0, proc: 0, tpc: 0, damage: 0, src: 'tally' };
    const rateSeen = seen ? seen.casts / (seen.secs / 60) : 0;
    // How far the cooldown binds: the casts seen against the most its cooldown (now) allows; half of it for one whose
    // cooldown events cut or reset (ev). Where it binds, a shorter (bought) cooldown gave casts - taken out for the rate.
    const b0 = seen && cdLive > 0 ? Math.min(1, rateSeen / (60 / cdLive * x)) : prior && cdLive > 0 ? 1 : 0.5;
    const bound = b0 * (ml[7] ? IMP.EV_BOUND : 1);
    const rateRef = rateSeen * (1 - bound + bound / inv);
    const ks = IMP.K_SECS, kc = IMP.K_CASTS, n = seen ? seen.secs : 0, nc = seen && seen.src === 'seen' ? seen.casts : 0;
    const p = prior || { c: rateRef, hpu: nc ? seen.hits / nc : 1, tpc: 1, pc: 1, passive: 0, dph: 0, D: 0 };
    const c = (p.c * ks + rateRef * n) / (ks + n);
    const hpu = nc ? (p.hpu * kc + seen.hits) / (kc + nc) : p.hpu;
    const tpc = nc && seen.tpc ? (p.tpc * kc + seen.tpc * nc) / (kc + nc) : p.tpc;
    const pc = nc && seen.hits ? (p.pc * kc + seen.proc / seen.hits * nc) / (kc + nc) : p.pc;
    // Iteration 40: what a hit LANDS against the formula's hit (landing, below): the seen damage a hit with the memory's
    // levels and its amps taken out, over the formula's - blended with 1 by the hits seen (K_LAND).
    const ampNow = gemAmp(k.gems, hero);
    const landSeen = nc && seen.hits > 0 && seen.damage > 0 && p.dph > 0 ?
      Math.min(IMP.LAND_MAX, Math.max(IMP.LAND_MIN, seen.damage / seen.hits / (lvlAvg[k.type] || lvlX(ml[0], level)) / ampNow / p.dph)) : null;
    const land = landSeen != null ? (IMP.K_LAND + landSeen * seen.hits) / (IMP.K_LAND + seen.hits) : 1;
    const h = c * hpu + p.passive, D = h * p.dph * land;
    const mult = lvlX(ml[0], level), DL = D * mult;
    const chanCost = c * chan * basicDps * (1 + IMP.RISK_W);
    const sust1 = ml[2] * statOf(hero, ml[6]) * c, sustL = sust1 * lvlX(ml[3], level);
    const cc = 1 + (ml[4] ? IMP.CC_W : 0);
    rows.push({ slot: k.slot, type: k.type, key, level, mult, ml, cdLive, cdRef, inv, rarity: k.rarity, gems: k.gems || [], prior, seen,
      rateSeen, rateRef, c, hpu, tpc, h, p: h * pc, D, DL, dBase: D, land, landSeen, chan, chanCost, sust: sust1, sustL, bound,
      impact: Math.max(0, D - chanCost) * cc + IMP.SUSTAIN_W * sust1, impactL: Math.max(0, DL - chanCost) * cc + IMP.SUSTAIN_W * sustL,
      w: n / (ks + n), src: !prior ? (seen ? `${seen.src}, no formula` : 'unknown') : seen ? `prior + ${seen.src} ${Math.round(100 * n / (ks + n))}%` : 'prior',
      seenD1: seen && seen.damage && seen.secs ? seen.damage / (seen.secs / 60) / (lvlAvg[k.type] || mult) : null });
  }
  return rows;
}
// What +dl levels add to a memory a minute: its reference damage (level 1, no essence) x the per-level multiplier - the
// same for every level (the game's scaling is linear), so the level it has matters through the price and the haste - its
// heals likewise, and the level's skill haste: the extra casts where the cooldown binds, each worth its damage at the new
// level less its channel (not for an Ultimate). ges: { haste, floor } read from the game.
function memUpGain(r, ges = {}, dl = 1) {
  const ml = r.ml;
  const dmg = r.dBase * ml[0] * dl;
  const sust = IMP.SUSTAIN_W * r.sust * ml[3] * dl;
  let haste = 0;
  if (!ml[5]) haste = Math.max(0, (r.DL ?? r.D) + dmg - (r.chanCost || 0)) * (cdmOf(ges, r.level) / cdmOf(ges, r.level + dl) - 1) * r.bound;
  return { gain: dmg + sust + haste, dmg, sust, haste };
}
// An essence's value (its size at a quality): (const + AP x ap + AD x ad) x (1 + per x quality) x k.
const gemSize = (e, hero, q) => (e[1] + e[2] * statOf(hero, 'p') + e[3] * statOf(hero, 'd')) * (1 + e[4] * Math.max(0, q)) * e[5];
// What an essence of this type and quality adds a minute in the memory `r` (memStats' row). 0: nothing counted.
function gemDpm(type, q, r, hero) {
  const e = GEM_EFF[gemKey(type)];
  if (!e || e[0] === 'O' || !r) return 0;
  const [form, , , , , , cap, n] = e;
  const v = gemSize(e, hero, q), perMin = x => cap > 0 ? Math.min(x, cap) : x, every = cap > 0 ? 60 / cap : Infinity;
  const aps = heroAps(hero), basic = 60 * aps * statOf(hero, 'd') * IMP.ATK_UPTIME;
  switch (form) {
    case 'H': return perMin(r.h) * v;
    case 'P': return perMin(r.p) * v;
    case 'M': return perMin(r.h) * v * ((hero && hero.maxHp) || 0);
    case 'C': return perMin(r.c) * v;
    case 'E': return Math.min(r.c, every) * v * (n ? Math.max(1, r.tpc) : 1);
    case 'X': return Math.min(r.c, every) * (r.c > 0 ? r.D / r.c : 0) * v;
    case 'A': return r.D * v;
    // Haste: more casts, each worth its damage less its channel (the channel is the memory's; an on-cast essence's value
    // comes per cast whatever the channel).
    case 'S': return Math.max(0, r.D - (r.chanCost || 0)) * r.bound * v / 100;
    case 'T': return Math.max(0, r.D - (r.chanCost || 0)) * r.bound * Math.min(1, v * aps);
    case 'W': return basic * v / 100 * Math.min(1, r.c * cap / 60);
    case 'K': return 60 * aps * IMP.ATK_UPTIME * v;
    case 'LC': return IMP.SUSTAIN_W * r.c * v;
    case 'LA': return IMP.SUSTAIN_W * r.D * v;
    case 'LH': return IMP.SUSTAIN_W * r.h * v;
    default: return 0;
  }
}
// What +dq quality adds to that essence a minute: its value x per x dq / (1 + per x quality).
function gemUpGain(g, r, hero, dq) {
  const e = GEM_EFF[gemKey(g.type)];
  if (!e || e[0] === 'O' || !(e[4] > 0) || (g.fit && g.fit.verdict === 'Dead')) return { gain: 0, dpm: 0 };
  const q = g.quality ?? 100, dpm = gemDpm(g.type, q, r, hero);
  return { gain: dpm * e[4] * dq / (1 + e[4] * Math.max(0, q)), dpm, q };
}
// Every candidate: each worn memory's +dl level(s) and each socketed essence's +dq quality, with its gain a minute and
// cost (o.memCost: slot -> dust, o.gemCost: essence id -> dust; none: free, the soul).
function upgradeCands(hero, rows, o = {}) {
  const out = [];
  for (const r of rows) {
    const g = memUpGain(r, o.ges, o.dl || 1);
    out.push({ kind: 'mem', slot: r.slot, name: `${r.slot} ${r.key.replace(/^[A-Z]+_/, '')}`, type: r.type, level: r.level, gain: g.gain, parts: g, impact: r.impact, src: r.src, uses: r.c, chan: r.chan, row: r,
      cost: o.memCost ? o.memCost.get(r.slot) : 0 });
    for (const gm of r.gems.filter(x => x && x.type)) {
      const u = gemUpGain(gm, r, hero, o.dq ?? IMP.GEM_DQ);
      out.push({ kind: 'gem', slot: r.slot, index: gm.index, id: gm.id, type: gm.type, name: `${gemKey(gm.type).replace(/^[A-Z]_/, '')} (${r.slot})`, quality: u.q ?? gm.quality,
        gain: u.gain, dpm: u.dpm, dead: !!(gm.fit && gm.fit.verdict === 'Dead'), form: (GEM_EFF[gemKey(gm.type)] || ['?'])[0],
        cost: o.gemCost ? o.gemCost.get(gm.id) : 0 });
    }
  }
  return out;
}
// The pick: the best gain a dust among the affordable (a well), or the best gain (free: the soul). null: nothing gains.
function pickUpgrade(cands, dust, free = false) {
  const ok = cands.filter(c => c.gain > 0 && (free || (c.cost > 0 && c.cost <= dust)));
  ok.sort((a, b) => free ? b.gain - a.gain : b.gain / b.cost - a.gain / a.cost || b.gain - a.gain);
  return ok[0] || null;
}
// --- iteration 53: the dust budget ---
// The user: "the bot does not know how to SAVE the dream dust - it dumps it on whatever, even when it has no memories or
// essences worth it yet; with no worthy memory it still upgrades one; early on I keep seeing it max Precision Shot, which is
// junk." Until now every well spent the dust down to the last affordable click (pickUpgrade: the best gain a dust, no bar)
// and the soul took the best gain. Runs 049-056: R got 32 dust clicks (~2040 dust) and 10 of the 28 souls, though its
// impact at level 1 after its channel (0.64-1.05 s a cast, ~800-1100 a minute of basic attacks lost) was the lowest worn
// memory's in every run 051-056 (45-500/min against Q's 830-1100): its +level gain (+25% of its gross damage) is on a par
// with Q's, and early nothing else is there (Q and R are Lacerta's own, W/E the first commons). Meanwhile run-054 ran out
// at Dark Bolt's 16.6/dust (26.3 on the first click) after 280 dust in DarkCave on R, Chomp and Shadow Walk (both dismantled
// later: their levels lost); run-052 spent 957 in Despair at 2.8-3.8/dust (R 6-9, Q 7-9) and met Stygian Rush at 20.2/dust
// with 24 dust.
// The game (DewGameplayExperienceSettings, read from the bundle - Formula strings): a memory level costs 30 + 10 L (L its
// level) and adds 25% of its level-1 value; an essence click costs round(35 + 0.1 q) and adds 50 quality (+0.5 of its
// quality-0 value for the 1%-a-point ones); dismantling a memory gives (30 + 1.5 L) x rarity (C 0.75, R 1.25, E 1.5, L 2) -
// the levels bought in a W/E memory that is replaced are lost (a level-5 Rare: 46 dust back for 220 spent); an essence
// keeps its quality wherever it moves. Primus's start room has a well: dust saved is never lost if the run gets there.
// The policy, at every well click (one line each):
// - U, the yardstick: what a par memory's first level gives a dust - 25% of PAR_AP x AP a minute for 40 dust (Q Incendiary
//   Rounds' fair level-1 damage in runs 051-056: 30-40 x AP, 35.5 on average; U = 6.3/dust at AP 28). A candidate's worth
//   V = its gain x keep / its cost / U.
// - junk: a memory whose impact at level 1 (its damage less its channel's cost, + heals) is below JUNK x par gets no dust
//   and no soul (R in 7 of the 8 runs); only the final zone's well spends on junk, and last.
// - keep: a W/E memory's level is worth KEEP[zone] of its gain (a W/E memory worn in zone 0 was replaced later in 14 of 17
//   cases in runs 049-056, in zone 1 2 of 7) - 1 for Q/R, essences, a W/E memory already STRONG x par at its level, and
//   in the final zone.
// - the bar: a buy needs V >= BAR[zone] (the zone's index; the final zone 0 - everything left goes there); one that would
//   leave less than RESERVE[zone] needs RES_X x the bar - the dust a new memory or essence found in the next rooms can take
//   at once. The best V that clears its bar is bought; none: saved, said with the best buy and the bar.
// Replayed on runs 049-056's wells (the logged candidates, the game's cost formulas, the model's own gains x the minutes
// each lasted): R's dust clicks 32 -> 8; the bar and reserve cost ~3-5% of the upgrades' damage-minutes against spending it
// all (dust held when a run died: 049 +247, 053 +496, 055 +139), the junk rule ~11% more by the model's own count - which
// credits each R level with +25% of R's gross damage while its channel's cost (the reason R is junk) stays. A judgement the
// user asked for, not a number the model gives.
const DUST = {
  PAR_AP: 36, AP_DEF: 28,
  BAR: [0.7, 0.7, 0.6, 0.55],       // U, by the zone's index (beyond: the last); the final zone 0
  RESERVE: [0, 100, 150, 150], RES_X: 1.25,
  KEEP: [0.5, 0.7, 0.8, 0.95],     // a W/E memory's level, by the zone's index
  STRONG: 2, JUNK: 0.5,
  COST_L: [30, 10], COST_Q: [35, 0.1],   // the game's formulas (30+10*x, 35+0.1*x) - the live getters decide at the well
};
const dustAt = (arr, z) => arr[Math.min(Math.max(0, z | 0), arr.length - 1)];
const dustPar = hero => DUST.PAR_AP * (statOf(hero, 'p') || DUST.AP_DEF);
const dustUnit = hero => dustPar(hero) * 0.25 / 40;
const memCostOf = L => DUST.COST_L[0] + DUST.COST_L[1] * L;
const gemCostOf = q => Math.round(DUST.COST_Q[0] + DUST.COST_Q[1] * q);
// Each candidate valued for the dust: { c, v (U), keep, junk, cost }. o: { zone, final }.
function dustRate(cands, hero, o = {}) {
  const par = dustPar(hero), U = dustUnit(hero), z = o.zone ?? 0;
  return cands.map(c => {
    const we = c.kind === 'mem' && (c.slot === 'W' || c.slot === 'E');
    const keep = !we || o.final || ((c.row && c.row.impactL) || 0) >= DUST.STRONG * par ? 1 : dustAt(DUST.KEEP, z);
    const junk = c.kind === 'mem' && (c.impact || 0) < DUST.JUNK * par;
    return { c, keep, junk, cost: c.cost, v: c.cost > 0 ? c.gain * keep / c.cost / U : 0 };
  });
}
// The judgement for one click: { buy, bar, rated, top, why } - buy null: the dust is saved (top: the best buy it had).
function dustJudge(cands, dust, hero, o = {}) {
  const z = o.zone ?? 0, rated = dustRate(cands, hero, o);
  const aff = rated.filter(x => x.c.gain > 0 && x.cost > 0 && x.cost <= dust);
  let pool = aff.filter(x => !x.junk);
  if (!pool.length && o.final) pool = aff;
  pool.sort((a, b) => b.v - a.v || b.c.gain - a.c.gain);
  const resv = x => !o.final && dust - x.cost < dustAt(DUST.RESERVE, z);
  const barOf = x => o.final ? 0 : dustAt(DUST.BAR, z) * (resv(x) ? DUST.RES_X : 1);
  const buy = pool.find(x => x.v >= barOf(x)) || null;
  const top = pool[0] || null, at = buy || top;
  return { buy, top, zone: z, bar: at ? barOf(at) : o.final ? 0 : dustAt(DUST.BAR, z), reserve: at ? resv(at) : false, rated, junk: rated.filter(x => x.junk),
    why: buy ? null : !aff.length ? 'nothing affordable' : !pool.length ? 'only junk affordable' : 'below the bar' };
}
// The clicks the policy would make with this dust (the game's cost formulas, a level's gain the same, an essence's falling
// by its quality) - for the walk to a well and the trip to a well room. { buys: [{ name, cost, v }], spend }.
function dustPlan(cands, dust, hero, o = {}) {
  const st = cands.map(c => ({ ...c }));
  const buys = [];
  for (let i = 0; i < 30; i++) {
    const j = dustJudge(st, dust, hero, o);
    if (!j.buy) break;
    const c = j.buy.c;
    buys.push({ name: c.name, cost: c.cost, v: j.buy.v });
    dust -= c.cost;
    if (c.kind === 'mem') { c.level = (c.level || 1) + 1; c.cost = memCostOf(c.level); }
    else {
      const e = GEM_EFF[gemKey(c.type)] || [], per = e[4] || 0.01, q = c.quality ?? 100, dq = o.dq ?? IMP.GEM_DQ;
      c.gain = c.gain * (1 + per * q) / (1 + per * (q + dq)); c.quality = q + dq; c.cost = gemCostOf(c.quality);
    }
  }
  return { buys, spend: buys.reduce((a, b) => a + b.cost, 0) };
}
// The soul (free): the best gain x keep, junk only when nothing else gains.
function soulPick(cands, hero, o = {}) {
  const rated = dustRate(cands, hero, o).filter(x => x.c.gain > 0);
  const pool = rated.some(x => !x.junk) ? rated.filter(x => !x.junk) : rated;
  pool.sort((a, b) => b.c.gain * b.keep - a.c.gain * a.keep);
  return pool.map(x => x.c);
}
const perDust = x => x.c.gain * x.keep / x.cost;
const junkSay = (j, hero) => j.junk.length ? ` (junk, no dust: ${j.junk.map(x => `${x.c.name} ${rnd(x.c.impact)}/min at lvl 1`).join(', ')} < ${DUST.JUNK} par ${rnd(DUST.JUNK * dustPar(hero))})` : '';
// "dust: upgrade E DarkBolt for 80, 26.3/dust (4.2U, bar 0.55U)" / "dust: saved 269 - best buy Q IncendiaryRounds at
// 3.9/dust (0.66U) below bar 4.1/dust (0.7U x1.25: it would leave less than 100) (junk, no dust: R PrecisionShot ...)".
function dustLine(j, dust, hero) {
  const U = dustUnit(hero), keepSay = x => x.keep < 1 ? `, x${x.keep} kept (a W/E level: lost if it is replaced)` : '';
  if (j.buy) return `dust: upgrade ${j.buy.c.kind === 'gem' ? 'essence ' : ''}${j.buy.c.name} for ${j.buy.cost}, ${perDust(j.buy).toFixed(1)}/dust (${j.buy.v.toFixed(2)}U, bar ${j.bar.toFixed(2)}U${keepSay(j.buy)})`;
  if (!j.top) return `dust: saved ${dust} - ${j.why}${junkSay(j, hero)}`;
  const res = j.reserve ? ` x${DUST.RES_X}: it would leave less than the ${dustAt(DUST.RESERVE, j.zone)} kept for what the next rooms bring` : '';
  return `dust: saved ${dust} - best buy ${j.top.c.kind === 'gem' ? 'essence ' : ''}${j.top.c.name} at ${perDust(j.top).toFixed(1)}/dust (${j.top.v.toFixed(2)}U${keepSay(j.top)}) below bar ${(j.bar * U).toFixed(1)}/dust (${j.bar.toFixed(2)}U${res})${junkSay(j, hero)}`;
}
// --- end dust budget ---
const rnd = x => Math.round(x || 0);
const n2 = x => (x || 0) >= 10 ? (x || 0).toFixed(1) : (x || 0).toFixed(2);
// "E DarkBolt +2070 dpm for 40 dust (51.8/dust) [lvl 1, 6901/min seen] (vs Q IncendiaryRounds +241/40 [lvl 1, 845/min seen],
// R PrecisionShot +530/160 [lvl 13, 6553/min seen], Void (W) +55/60 [q100 C, 440/min])": the gain a minute, the cost, and
// what it rests on (a memory: its level and impact a minute and where that came from; an essence: its quality, form and
// what it adds a minute now).
function upgradeLine(best, cands, free = false) {
  const detail = c => c.kind === 'gem' ? `[q${c.quality ?? '?'} ${c.dead ? 'dead' : c.form}${c.dpm ? `, ${rnd(c.dpm)}/min` : ''}]` :
    `[lvl ${c.level}, ${useSay(c.row)}${landSay(c.row)}${c.chan ? `, ${c.chan.toFixed(2)} s channel` : ''}, ${rnd(c.impact)}/min at lvl 1${c.row && c.row.seenD1 != null ? ` (seen ~${rnd(c.row.seenD1)})` : ''}]`;
  const say = c => `${c.name} +${rnd(c.gain)}${free ? '' : c.cost ? `/${c.cost}` : '/?'} ${detail(c)}`;
  const rest = cands.filter(c => c !== best && (c.kind === 'mem' || c.gain > 0 || c.cost)).map(say).join(', ');
  if (!best) return `nothing gains (${rest})`;
  return `${best.kind === 'gem' ? 'essence ' : ''}${best.name} +${rnd(best.gain)} dpm${free ? '' : ` for ${best.cost} dust (${(best.gain / best.cost).toFixed(1)}/dust)`} ${detail(best)} (vs ${rest || 'nothing else'})`;
}
// A new memory against the worn W/E ones: { slot, newImp, oldImp, old } to replace, or { none, why }. item: { type, level,
// chan (the seconds a cast holds the hero, when read) }. The new one at the level it has, by the dump alone (its cooldown
// with the hero's ability haste, its hits, the formula's damage); the worn ones at theirs, by memStats' reference rates and
// the formula - the levels live on the memory (SkillTrigger.level; HeroSkill.UnequipSkill drops the memory with them),
// so they are what a swap loses; the essences live on the slot (EquipSkill re-parents them to the new memory), so they
// count for neither.
// Iteration 47 (orchestrator hotfix): what each memory was seen to do while worn this run (memStats' impactL at its level).
// run-053 swapped E Static Discharge (149/min worn, prior + seen 90%) for Searing Charge, then picked Static Discharge up
// again at once - off the ground it had only the prior (7.1 uses/min x 2 hits x 248 = 4815/min) - three times, each
// leaving a memory on the ground: travel refused ("Uncollected Memory"), the run stuck. A memory seen worn is judged by
// what it was seen to do, the prior only for one never worn.
const memSeen = new Map();
function memoryWanted(hero, rows, item, slots = ['W', 'E']) {
  for (const r of rows) if (r && r.key && r.impactL != null && r.src && r.src !== 'prior') memSeen.set(r.key, { imp: r.impactL, level: r.level || 1, src: r.src });
  const key = memKey(item.type), pr0 = MEM_PRIOR[key];
  if (!pr0) return { none: true, why: 'unknown', unknown: true };
  const ml = MEM_LVL[key] || MEM_LVL_DEF, L = item.level || 1, chan = item.chan > 0 ? item.chan : 0;
  const haste = (hero && hero.stats && hero.stats.abilityHaste) || 0;
  const pr = priorUse(item.type, hero, pr0[0] / (1 + haste / 100), chan);
  const chanCost = pr.c * chan * heroAps(hero) * statOf(hero, 'd') * (1 + IMP.RISK_W);
  const sust = ml[2] * statOf(hero, ml[6]) * lvlX(ml[3], L) * pr.c;
  const seen = memSeen.get(key);
  const newImp = seen ? seen.imp * lvlX(ml[0], L) / lvlX(ml[0], seen.level)
    : Math.max(0, pr.D * lvlX(ml[0], L) - chanCost) * (1 + (ml[4] ? IMP.CC_W : 0)) + IMP.SUSTAIN_W * sust;
  const own = rows.filter(r => slots.includes(r.slot) && r.rarity !== 'Character').sort((a, b) => a.impactL - b.impactL);
  if (!own.length) return { none: true, why: 'no W/E memory to replace', newImp };
  const old = own[0];
  const says = seen ? `${rnd(newImp)}/min at lvl ${L} (as seen worn this run: ${rnd(seen.imp)} at lvl ${seen.level}, ${seen.src})`
    : `${rnd(newImp)}/min at lvl ${L} (prior: ${pr.c.toFixed(1)} uses/min x ${n2(pr.hpu)} hits x ${rnd(pr.dph)})`;
  const olds = `${old.slot} ${old.key}'s ${rnd(old.impactL)} at lvl ${old.level} (${old.src})`;
  if (!(newImp > old.impactL * IMP.REPLACE_BY)) return { none: true, why: `${says} is not ${IMP.REPLACE_BY}x ${olds}`, newImp, old };
  return { slot: old.slot, newImp, oldImp: old.impactL, old, why: `${says} against ${olds}` };
}
// The seconds each memory's casts held the hero, as the bot cast them (fight()): Precision Shot's charge as sent + its
// cast daze, another charged skill's charge, a channel's lock (learnLock). chanOf: type -> the average a cast.
const chanTally = { n: {}, s: {} };
const tallyChan = (type, secs, t = chanTally) => { t.n[type] = (t.n[type] || 0) + 1; t.s[type] = (t.s[type] || 0) + Math.max(0, secs || 0); };
const chanOf = (t = chanTally) => Object.fromEntries(Object.keys(t.n).filter(k => t.n[k] > 0).map(k => [k, t.s[k] / t.n[k]]));
// "uses/min 8.9 prior, 4.4 seen -> 5.1": the prior's rate, the seen one (bought haste taken out), the blend.
const useSay = r => !r ? '' : `uses/min ${r.prior ? r.prior.c.toFixed(1) : '?'} prior${r.seen ? `, ${r.rateRef.toFixed(1)} ${r.seen.src}` : ''} -> ${r.c.toFixed(1)}`;
// Each memory: its rates (prior, seen, blend), hits a use, channel, damage a minute at level 1 by the formula (the seen one
// at level 1 beside it) and at its level.
const landSay = r => r && r.landSeen != null ? ` x ${r.land.toFixed(2)} landed (seen ${r.landSeen.toFixed(2)})` : '';
const impactLine = rows => rows.map(r => `${r.slot} ${r.key} lvl ${r.level}: ${useSay(r)}, ${n2(r.hpu)} hits a use x ${rnd(r.prior ? r.prior.dph : 0)}${landSay(r)}` +
  `${r.chan ? `, ${r.chan.toFixed(2)} s channel (-${rnd(r.chanCost)})` : ''}, ${rnd(r.impact)}/min at lvl 1${r.seenD1 != null ? ` (seen ~${rnd(r.seenD1)})` : ''}, ${rnd(r.impactL)} at lvl ${r.level}`).join('; ');
// ----- end of iteration 37's pure part ---------------------------------------------------------------------------------
// --- essence keep (iteration 25) ---
// The user: "The bot dismantles essences it finds no use for, instead of keeping them for later memories; and an
// essence can become useful when another essence provides the missing trigger." Decompiled (history/it25):
// - HeroSkill.EquipGem asks nothing of the memory: any essence goes into any free socket (one of a type at a time -
//   picking up a second one of a type merges it into the first, Gem.OnInteract).
// - A socket belongs to the slot (GemLocation = slot + index), not to the memory: EquipSkill/UnequipSkill re-parent
//   the slot's essences to whatever memory goes there. A memory replaced leaves its essences to the new one - so an
//   essence dead in W or E today can fire with the next W or E (Q and R, Lacerta's own, never change).
// - EditSkillManager.DoClickOnGemSlot: in the plain edit screen (LeftCtrl held - editSkillHold) a click on a socket
//   takes its essence out onto the ground at the hero's feet (CmdUnequipGem, within 1.5 m); with an essence in hand
//   (EquipGem) a click on a taken socket drops the one there and puts the held one in. Out, it keeps its id.
// - Travel is refused while holding anything (ZoneManager.GetCannotTravelReason); an essence left on the ground is
//   saved with its room (Gem.ShouldBeSavedWithRoom) - found again only by walking back. Keeping one = socketing it.
// AreMyGemsCompatible's `missing` (Verdict.For(gem, skill, out missing)): the element a Dead essence's damage trigger
// answers to (Frost: Cold) that the memory's damage lacks; None otherwise - then Dead means the memory never does
// what the essence waits for (its needs: Damage, Heal, Shield, Cast). The verdict already counts the essences beside
// it in that memory (SuppliedBySiblings: a sibling that fires on the cast, in a memory that is cast, and supplies the
// need; ElementChangers.AddedFor: a sibling's written elements, any sibling supplying damage = unknown = fine).
// DevTools' /hero/fit now also sends fit.missing, the essence's profile (needs, supplies, gate, adds), each memory's
// `does`, and the essences in each with their verdicts and profiles. So "would X fire in M if Y were there" is
// wakes(Y's profile, X's missing in M, M's does).
// The choice for an essence in hand (planHeld), by the number of essences it makes fire: itself where it is live,
// plus the Dead ones beside it that it wakes (pairing). A free socket first; a socket holding a Dead essence only
// when that gains more (the Dead one comes out and goes through the same choice). Nothing to gain: kept (parked) in
// the free socket most likely to come alive - W/E (the memory may change), cast, the most room, the least rare memory
// - never R (Precision Shot: its sockets are for essences that fit it). Every socket but R's taken: it replaces the
// weakest parked Dead one if it is rarer, else it is dismantled.
const DEAD_HARM = new Set(['Gem_E_Apathy']);   // Dead, only its drawback is left (ElementGates' note): never parked
const REPLACEABLE = ['W', 'E'];                 // the slots sortHands replaces memories in
const GEM_RANK = t => { const m = /^Gem_([CREUL])_/.exec(t || ''); return m ? { C: 0, R: 1, E: 2, L: 3, U: 3 }[m[1]] : 0; };
const has = (list, x) => Array.isArray(list) && list.includes(x);
const meets = (a, b) => Array.isArray(a) && Array.isArray(b) && a.some(x => b.includes(x));
const gemMissing = g => (g && g.fit && g.fit.missing) || null;
// Would an essence with profile `p`, in a memory that `does`, give a Dead essence what it is `missing`? null: cannot
// be told (no profile or no missing - an older DevTools).
function wakes(p, missing, does) {
  if (!p || p.error || !missing) return null;
  if (Array.isArray(missing.element) && missing.element.length) {
    if (has(p.supplies, 'Damage') || p.adds === null) return true;   // damage of no known element: fine by the mod
    return meets(p.adds, missing.element);
  }
  if (!Array.isArray(missing.needs) || !missing.needs.length) return null;
  if (does && does.cast === false) return false;                        // a passive memory starts nothing beside it
  if (!p.alwaysLive && !has(p.needs, 'Cast')) return false;             // it has to fire on the cast
  return meets(p.supplies, missing.needs);
}
// Would essence `z` be Dead without `y` beside it in a memory that `does`? (Before moving y away.)
function leansOn(z, y, does) {
  if (!z || !y || z.alwaysLive) return false;
  if (!does || does.error) return true;
  const done = ['Damage', 'Heal', 'Shield', 'Cast'].filter(k => does[k.toLowerCase()]);
  if (Array.isArray(z.needs) && z.needs.length && !meets(z.needs, done) && meets(z.needs, y.supplies)) return true;
  if (Array.isArray(z.gate) && z.gate.length && !(does.elements === null || meets(z.gate, does.elements)) &&
    (has(y.supplies, 'Damage') || y.adds === null || meets(y.adds, z.gate))) return true;
  return false;
}
// The essences in a slot: /hero/fit's (with profiles) or /hero's (verdicts only).
function slotGems(hero, s) {
  if (Array.isArray(s.gems)) return s.gems.filter(g => g && g.type);
  const k = ((hero && hero.skills) || []).find(x => x.slot === s.slot);
  return ((k && k.gems) || []).filter(g => g.type);
}
const freeIndex = (hero, s) => { const used = new Set(slotGems(hero, s).map(g => g.index)); let i = 0; while (used.has(i)) i++; return i; };
const fitOrder = s => { const i = FIT_ORDER.indexOf(s.slot); return i < 0 ? 99 : i; };
// Best first: essences made to fire, the moved one itself live (iteration 40: then the value, Fine over unknown, spreading - below).
// Iteration 29: then the worth of what fires (optVal: each essence's value there over its best, summed) - a difference
// under VALUE_TIE is a tie; without values (none known) the old order decides as before.
const valCmp = (a, b) => a.val == null || b.val == null || Math.abs(a.val - b.val) < VALUE_TIE ? 0 : b.val - a.val;
// An essence that cares about nothing in the memory: the socket worth least to the others (spare = 1 - worth).
const spareCmp = (a, b) => a.spare == null || b.spare == null || Math.abs(a.spare - b.spare) < VALUE_TIE ? 0 : b.spare - a.spare;
// Iteration 40: no slot favouritism in a tie (until now R first - FIT_ORDER - then the rarer, the higher-level memory: run-049's
// Epiphany, 1.00 in Q, E and R, went to R, its 4th essence, with Q and W empty). A tie goes to the memory with the fewest
// essences (spreading), then the most free sockets, then the plain slot order Q, W, E, R.
const SLOT_ORDER = ['Q', 'W', 'E', 'R'];
const slotOrder = s => { const i = SLOT_ORDER.indexOf(s.slot); return i < 0 ? 99 : i; };
const gemsIn = s => Math.max(0, (s.sockets || 0) - (s.free || 0));
const keepCmp = (a, b) => b.gain - a.gain || b.live - a.live || valCmp(a, b) || spareCmp(a, b) ||
  (fitVerdict(b.s) === 'Fine') - (fitVerdict(a.s) === 'Fine') || gemsIn(a.s) - gemsIn(b.s) || (b.s.free || 0) - (a.s.free || 0) || slotOrder(a.s) - slotOrder(b.s);
// Where a Dead essence waits best: a memory that may be replaced, one that is cast (a sibling can only wake it there),
// room for its partner, the least rare memory. Never R.
const parkScore = (s, missing) => (REPLACEABLE.includes(s.slot) && s.rarity !== 'Character' ? 3 : 0) +
  (s.does && s.does.cast === false && !(missing && missing.element && missing.element.length) ? -2 : 0) +
  0.5 * Math.min(s.free, 3) - 0.5 * (FIT_RANK[s.rarity] ?? 0);
// The essence in hand (or one on the ground, its /hero/fit by id): { act: 'socket', slot, index, why: live|pair|park,
// woken } | { act: 'replace', slot, index, out, why } (a click on a taken socket - `out` comes out) | { act: 'dismantle', why }.
function planHeld(hero, fit, item = {}, ctx = {}) {
  const type = item.type || (fit && fit.gem && fit.gem.type) || '';
  const prof = (fit && fit.gem && fit.gem.profile) || null;
  const slots = fitSlots(hero, fit);
  // Iteration 29: what each worn memory does a minute (memUse) and what the held essence gets from each (rel 0..1).
  const use = memUse(slots, hero, ctx);   // ctx: the run's tallies by default (tests and replays pass their own)
  const rv = relValues(type, prof, fit && fit.gem && fit.gem.limits, slots, use, s => fitVerdict(s) === 'Dead', item.quality ?? (fit && fit.gem && fit.gem.quality));
  const optVal = o => optValue(o, rv, slots, use);
  const woken = (s, but) => slotGems(hero, s).filter(g => g !== but && fitVerdict(g) === 'Dead' && wakes(prof, gemMissing(g), s.does) === true);
  const pick = (o, act, why) => ({ act, slot: o.s.slot, index: o.index, memory: o.s.memory, verdict: fitVerdict(o.s), why, woken: o.woken.map(g => g.type), out: o.out || null,
    value: o.val ?? null, rv, use, slots });
  const free = [], swap = [];
  for (const s of slots) {
    const live = fitVerdict(s) === 'Dead' ? 0 : 1;
    if (s.free > 0) {
      const w = woken(s);
      if (live + w.length > 0) free.push({ s, live, gain: live + w.length, woken: w, index: freeIndex(hero, s) });
    }
    // A socket holding a Dead essence that the held one does not wake: the held one there, that one out.
    for (const g of slotGems(hero, s)) {
      if (fitVerdict(g) !== 'Dead' || wakes(prof, gemMissing(g), s.does) === true) continue;
      const w = woken(s, g);
      if (live + w.length > 0) swap.push({ s, live, gain: live + w.length, woken: w, index: g.index, out: { slot: s.slot, index: g.index, id: g.id, type: g.type } });
    }
  }
  for (const o of free.concat(swap)) { o.val = optVal(o); if (rv && rv.kind.on === 'o' && use.get(o.s.slot)) o.spare = 1 - use.get(o.s.slot).worth; }
  free.sort(keepCmp); swap.sort(keepCmp);
  // In place of a Dead one when that makes more fire - or as many, but worth VALUE_SWAP more (a free socket only in a
  // memory that gives it little, a Dead one sitting where it would give most).
  if (swap.length && (swap[0].gain > (free.length ? free[0].gain : 0) ||
    (free.length && swap[0].gain === free[0].gain && swap[0].live >= free[0].live && swap[0].val != null && free[0].val != null && swap[0].val - free[0].val >= VALUE_SWAP)))
    return pick(swap[0], 'replace', swap[0].live ? 'live' : 'pair');
  if (free.length) return pick(free[0], 'socket', free[0].woken.length ? 'pair' : 'live');
  // Nothing fires with it anywhere now: kept for later, or dust.
  if (DEAD_HARM.has(type)) return { act: 'dismantle', why: 'harm' };
  const missing = slots.map(s => s.fit && s.fit.missing).find(Boolean) || null;
  const park = slots.filter(s => s.free > 0 && s.slot !== 'R').map(s => ({ s, live: 0, gain: 0, woken: [], index: freeIndex(hero, s), score: parkScore(s, missing) }));
  if (park.length) return pick(park.sort((a, b) => b.score - a.score || fitOrder(a.s) - fitOrder(b.s))[0], 'socket', 'park');
  // Every socket but R's taken: in place of the weakest parked Dead one (not one it would wake) if this is rarer (or
  // better), else dust.
  const parked = [];
  for (const s of slots) if (s.slot !== 'R') for (const g of slotGems(hero, s)) if (fitVerdict(g) === 'Dead' && wakes(prof, gemMissing(g), s.does) !== true) parked.push({ s, g });
  parked.sort((a, b) => GEM_RANK(a.g.type) - GEM_RANK(b.g.type) || (a.g.quality || 0) - (b.g.quality || 0));
  const weak = parked[0];
  if (weak && (GEM_RANK(type) > GEM_RANK(weak.g.type) || (GEM_RANK(type) === GEM_RANK(weak.g.type) && (item.quality || 0) > (weak.g.quality || 0) + 20)))
    return pick({ s: weak.s, index: weak.g.index, woken: [], out: { slot: weak.s.slot, index: weak.g.index, id: weak.g.id, type: weak.g.type } }, 'replace', 'park');
  return { act: 'dismantle', why: slots.some(s => s.free > 0) ? 'only R free' : 'full' };
}
// A socketed Dead essence `g`, with its /hero/fit (by id): a better socket elsewhere - live, or waking others - or,
// Dead in R with R full, a parking socket out of R. null: stays.
function planMove(hero, fit, g, ctx = {}) {
  if (!fit || !Array.isArray(fit.slots)) return null;
  const prof = (fit.gem && fit.gem.profile) || null;
  const opts = [];
  for (const s of fit.slots) {
    if (s.slot === g.slot || !(s.free > 0)) continue;
    const live = fitVerdict(s) === 'Dead' ? 0 : 1;
    const w = slotGems(hero, s).filter(x => fitVerdict(x) === 'Dead' && wakes(prof, gemMissing(x), s.does) === true);
    if (live + w.length > 0) opts.push({ s, live, gain: live + w.length, woken: w, index: freeIndex(hero, s) });
  }
  if (opts.length) {
    const slots = fit.slots, use = memUse(slots, hero, ctx);
    const rv = relValues(g.type, prof, fit.gem && fit.gem.limits, slots, use, s => fitVerdict(s) === 'Dead', g.quality);
    for (const o of opts) { o.val = optValue(o, rv, slots, use); if (rv && rv.kind.on === 'o' && use.get(o.s.slot)) o.spare = 1 - use.get(o.s.slot).worth; }
    const o = opts.sort(keepCmp)[0];
    const vtag = o.val != null ? ` (value ${o.val.toFixed(2)})` : '';
    return { gem: g, to: { slot: o.s.slot, index: o.index }, why: (o.live ? `${fitVerdict(o.s) || 'live'} in ${o.s.memory}` : `wakes ${o.woken.map(x => x.type).join(', ')} in ${o.s.memory}`) + vtag };
  }
  const r = fit.slots.find(s => s.slot === 'R');
  if (g.slot === 'R' && r && !(r.free > 0)) {
    const park = fit.slots.filter(s => s.slot !== 'R' && s.free > 0).sort((a, b) => parkScore(b, g.fit && g.fit.missing) - parkScore(a, g.fit && g.fit.missing));
    if (park.length) return { gem: g, to: { slot: park[0].slot, index: freeIndex(hero, park[0]) }, why: 'dead in R, R full - its socket kept for an essence that fits it' };
  }
  return null;
}
// Iteration 40 (the user: "when the bot picks up a new memory it then rates highest, it does not move essences over from
// the others"): the whole assignment of the socketed essences to the sockets, re-planned after every loadout change. Until
// now (iteration 29's planValueMove) one live essence at a time, only into a free socket where it gave >= 0.5 more of its
// best, once a run each, looked at every 3 minutes - and by the damage seen, so the moves it made were the bias itself
// (run-044: Sulfur and Confidence out of Dancing Blades - 3763 a minute at level 1 - into Precision Shot at level 8).
// Each essence's worth in each memory is its value on the fair reference (relValues: gemDpm on memUse's rows, damage a
// minute; an essence GEM_EFF does not value counts MOVE.NOMINAL x its rel), LIVE more when it fires there (its verdict for
// that memory - /hero/fit by its id), 0 when Dead. A move costs MOVE.DPM or MOVE.REL of what it gives where it is,
// whichever is more (the time the edit screen, the drop and the pick-up take, and a hysteresis against the estimates'
// drift). The best assignment under the sockets each memory has (exact: a table over the essences and the sockets taken
// in each memory) is then turned into moves into free sockets, the best gain first; one that needs a swap (both memories
// full) is said, not done. An essence a live one beside it leans on, or one waking a Dead one beside it, stays.
// hero: /hero; fits: id -> /hero/fit?id= (the verdicts for that essence in each memory). Pure.
const MOVE = { CAP: 3, DPM: 50, REL: 0.25, NOMINAL: 150, LIVE: 10000 };
function planAll(hero, fits, o = {}) {
  const any = [...((fits && fits.values()) || [])].find(f => f && Array.isArray(f.slots));
  if (!any) return null;
  const slots = any.slots.filter(s => s.sockets > 0);
  const use = o.use || memUse(slots, hero, o.ctx || {});
  const S = slots.map(s => s.slot);
  const ess = [];
  for (const s of slots) for (const g of slotGems(hero, s)) {
    if (!g.id) continue;
    const f = fits.get(g.id) || null;
    const verdictIn = sl => { const x = f && f.slots && f.slots.find(y => y.slot === sl); return x ? fitVerdict(x) : sl === s.slot ? fitVerdict(g) : null; };
    const prof = (f && f.gem && f.gem.profile) || g.profile || null;
    const rv = relValues(g.type, prof, f && f.gem && f.gem.limits, slots, use, x => verdictIn(x.slot) === 'Dead', g.quality);
    const val = sl => rv ? (rv.dpm ? rv.raw.get(sl) || 0 : MOVE.NOMINAL * (rv.rel.get(sl) || 0)) : 0;
    const score = new Map(S.map(sl => [sl, verdictIn(sl) === 'Dead' ? 0 : MOVE.LIVE + val(sl)]));
    const sibs = slotGems(hero, s).filter(z => z.id !== g.id);
    const pinned = sibs.some(z => fitVerdict(z) !== 'Dead' && leansOn(z.profile, prof, s.does)) ||
      sibs.some(z => fitVerdict(z) === 'Dead' && wakes(prof, gemMissing(z), s.does) === true);
    const cost = Math.max(MOVE.DPM, MOVE.REL * Math.max(0, score.get(s.slot) - MOVE.LIVE));
    ess.push({ g: { slot: s.slot, index: g.index, id: g.id, type: g.type, quality: g.quality, fit: g.fit }, from: s.slot, score, pinned, cost, rv });
  }
  if (!ess.length) return { moves: [], swaps: [], total: { dpm: 0, live: 0 }, best: { dpm: 0, live: 0 }, ess, use, slots };
  // Exact: best[i][taken per memory] over the essences in order (<= 5^4 states a step).
  const cap = S.map(sl => slots.find(s => s.slot === sl).sockets);
  const key = t => t.join(',');
  let layer = new Map([[key(S.map(() => 0)), { v: 0, pick: [] }]]);
  for (const e of ess) {
    const next = new Map();
    for (const [k, st] of layer) {
      const t = k.split(',').map(Number);
      S.forEach((sl, j) => {
        if (t[j] >= cap[j] || (e.pinned && sl !== e.from)) return;
        const v = st.v + e.score.get(sl) - (sl !== e.from ? e.cost : 0);
        const t2 = t.slice(); t2[j]++;
        const k2 = key(t2), cur = next.get(k2);
        if (!cur || v > cur.v + 1e-9) next.set(k2, { v, pick: st.pick.concat(sl) });
      });
    }
    layer = next;
  }
  const bestSt = [...layer.values()].sort((a, b) => b.v - a.v)[0];
  const sum = at => ess.reduce((a, e, i) => { const x = e.score.get(at(e, i)); return { dpm: a.dpm + Math.max(0, x - MOVE.LIVE), live: a.live + (x >= MOVE.LIVE ? 1 : 0) }; }, { dpm: 0, live: 0 });
  const total = sum(e => e.from), best = sum((e, i) => bestSt.pick[i]);
  const plan = ess.map((e, i) => ({ ...e, to: bestSt.pick[i] })).filter(e => e.to !== e.from)
    .map(e => ({ gem: e.g, from: e.from, to: e.to, gain: e.score.get(e.to) - e.score.get(e.from), cost: e.cost, rv: e.rv }));
  // Into free sockets, the best gain first; what is left needs a swap.
  const free = new Map(slots.map(s => [s.slot, s.free || 0]));
  const moves = [], left = plan.slice().sort((a, b) => b.gain - a.gain);
  for (let progress = true; progress && left.length;) {
    progress = false;
    for (let i = 0; i < left.length; i++) {
      const m = left[i];
      if (!(free.get(m.to) > 0)) continue;
      free.set(m.to, free.get(m.to) - 1); free.set(m.from, free.get(m.from) + 1);
      moves.push(m); left.splice(i, 1); progress = true; break;
    }
  }
  return { moves, swaps: left, total, best, ess, use, slots };
}
// "move Sulfur W1 -> R (+120 dpm)": the dpm part of an essence's worth (LIVE is the firing, said as "now fires").
const moveSay = m => `${gemKey(m.gem.type).replace(/^[A-Z]_/, '')} ${m.from}${m.gem.index} -> ${m.to} (${m.gain >= MOVE.LIVE / 2 ? 'now fires' : `${m.gain >= 0 ? '+' : ''}${Math.round(m.gain)}`})`;
const totalSay = t => `${Math.round(t.dpm)} dpm, ${t.live} firing`;
function planLine(p, event) {
  if (!p) return `essences: re-plan after ${event}: no verdicts`;
  const mv = p.moves.map(moveSay), sw = p.swaps.map(m => moveSay(m) + ' needs a swap');
  return `essences: re-plan after ${event}: ${mv.length || sw.length ? mv.concat(sw).join(', ') : 'all stay'}; total ${totalSay(p.total)} -> ${totalSay(p.best)}`;
}
// Pairing by moving the partner: a Dead essence X in a memory M with a free socket, and an essence Y elsewhere that
// would wake it there - Y Dead where it is (nothing lost), or live but not out of R and with nothing beside it that
// leans on it (then its own verdict in M is asked before the move: `ask`). `fit`: any /hero/fit (for the profiles).
function planPair(hero, fit) {
  if (!fit || !Array.isArray(fit.slots)) return [];
  const out = [];
  for (const m of fit.slots) {
    if (!(m.free > 0)) continue;
    for (const x of slotGems(hero, m)) {
      if (fitVerdict(x) !== 'Dead') continue;
      for (const s of fit.slots) {
        if (s.slot === m.slot) continue;
        for (const y of slotGems(hero, s)) {
          if (!y.profile || wakes(y.profile, gemMissing(x), m.does) !== true) continue;
          const dead = fitVerdict(y) === 'Dead';
          if (!dead && (s.slot === 'R' || slotGems(hero, s).some(z => z !== y && fitVerdict(z) !== 'Dead' && leansOn(z.profile, y.profile, s.does)))) continue;
          out.push({ gem: { slot: s.slot, index: y.index, id: y.id, type: y.type, fit: y.fit }, to: { slot: m.slot, index: freeIndex(hero, m) }, ask: !dead,
            why: `wakes ${x.type} in ${m.memory}${dead ? ` (dead in ${s.memory})` : ''}` });
        }
      }
    }
  }
  return out.sort((a, b) => a.ask - b.ask);
}
// For a merchant's or a shrine's essence: 'free' (fires in a free socket), 'pair' (wakes one there), 'swap' (fires in
// place of a Dead one), 'full' (live only where the sockets are taken), 'keep' (dead now, parked), 'dead' (dust),
// null (no verdict to go by).
function offerClass(fit, hero) {
  if (!fit || !Array.isArray(fit.slots) || !fit.slots.length) return null;
  if (fit.slots.every(s => fitVerdict(s) === null)) return null;
  const p = planHeld(hero, fit, { type: fit.gem && fit.gem.type });
  if (p.act === 'socket' && p.why !== 'park') return p.why === 'pair' ? 'pair' : 'free';
  if (p.act === 'replace' && p.why !== 'park') return 'swap';
  if (fit.slots.some(s => fitVerdict(s) !== 'Dead')) return 'full';
  return p.act === 'dismantle' ? 'dead' : 'keep';
}
// --- end essence keep ---
// /hero/fit, asked once (no retries). Only a DevTools without the route ("no route /hero/fit") turns it off for the
// run; any other error (iteration 24, run-038: a NullReferenceException at a Shrine_Enlightenment's three choices, a
// memory slot empty mid-swap - 3 in a row switched it off for the rest of the run) goes by free sockets for that call
// alone and asks again next time.
let fitMissing = false, fitSaid = 0;
async function heroFit(q) {
  if (fitMissing) return null;
  try { const r = await call('GET', '/hero/fit' + (q ? '?' + new URLSearchParams(q) : '')); return r && Array.isArray(r.slots) ? r : null; }
  catch (e) {
    const msg = String(e && e.message || e).split('\n')[0];
    if (/no route/i.test(msg)) { fitMissing = true; log(`  /hero/fit: ${msg} - essences go by free sockets alone from now on`); }
    else if (Date.now() - fitSaid > 10000) { fitSaid = Date.now(); log(`  /hero/fit: ${msg.slice(0, 120)} - free sockets for this one, asked again next time`); }
    return null;
  }
}
const missTag = m => !m ? '' : m.element && m.element.length ? `no ${m.element.join('/')} damage` : m.needs && m.needs.length ? `never ${m.needs.join('/')}` : '';
const fitLine = fit => fit && Array.isArray(fit.slots) ? fit.slots.map(s => `${s.slot} ${s.memory} ${fitVerdict(s) || '?'} (${s.free}/${s.sockets} free)${fitVerdict(s) === 'Dead' ? (missTag(s.fit.missing) ? ' - ' + missTag(s.fit.missing) : fitWhy(s) ? ' - ' + fitWhy(s).slice(0, 60) : '') : ''}`).join('; ') : 'no verdicts';

// Iteration 25: an essence that came out of its socket (a held one went in its place): kept by the same choice
// (planHeld, by id on the ground), or broken where it lies. With enemies about it is left to the loot.
async function takeBack(out, depth) {
  if (!out || !out.id || await enemyNear()) return;
  const fit = await heroFit({ id: out.id });
  const p = fit ? planHeld(await get('/hero'), fit, { type: out.type }) : { act: 'dismantle', why: 'no verdicts' };
  if (p.act === 'dismantle') {
    log(`  ${out.type} (out of ${out.slot} ${out.index}): dismantled - ${p.why} | ${fitLine(fit)}`);
    await tryPost('/hero/dismantle', { id: out.id });
    return;
  }
  for (let i = 0; i < 4; i++) {
    await tryPost('/hero/interact', { id: out.id });
    if (await waitGone(out.id, out.type, 1.2)) break;
  }
  await sortHands(depth);
}

// Iteration 25: moving a socketed essence, as a player does it - LeftCtrl held opens the plain edit screen
// (editSkillHold), a click on the socket takes the essence out onto the ground at the hero's feet (it keeps its id),
// then it is picked up and put into the other socket. Only with nothing in hand (opening the screen would drop it)
// and no enemy near. Two failures to open the screen and it is not tried again this run.
let moveFails = 0, dragMissing = false;
async function moveEssence(g, to, why) {
  if (moveFails >= 2 || !g || !g.id || await enemyNear()) return false;
  log(`move essence ${g.type} ${g.slot} ${g.index} -> ${to.slot} ${to.index} - ${why}`);
  let out = false, said = '', dragged = false;
  try {
    await tryPost('/input/key', { key: 'LeftCtrl', action: 'down' });
    for (let i = 0; i < 10 && !out; i++) {
      const st = await get('/state');
      if (st.edit && st.edit.mode === 'Regular') out = true; else await sleep(60);
    }
    // Iteration 40: a drag from socket to socket, as a player drags it (EditSkillManager.EndDrag -> HeroSkill.CmdSwapSlotGem:
    // no trip to the ground) when the build has the route (proposals/iter-40-mod.md); else the click that drops it.
    if (out && !dragMissing) {
      const d = await tryPost('/edit/drag', { slot: g.slot, index: g.index, toSlot: to.slot, toIndex: to.index });
      if (d.error && /no route/i.test(d.error)) dragMissing = true;
      else if (!d.error && !(d.refused && d.refused.length)) { dragged = true; }
      else log(`  drag refused: ${d.error || d.refused.join('; ')} - the click instead`);
    }
    if (out && !dragged) {
      const r = await tryPost('/edit/click', { slot: g.slot, index: g.index });
      if (r.error || (r.refused && r.refused.length)) { out = false; said = r.error || r.refused.join('; '); }
    } else if (!out) said = 'the edit screen did not open';
  } finally {
    await tryPost('/input/key', { key: 'LeftCtrl', action: 'up' });
  }
  if (!out) { moveFails++; log(`  not moved: ${said}`); return false; }
  if (dragged) {
    const h = await get('/hero'), k = (h.skills || []).find(x => x.slot === to.slot);
    const ok = !!(k && (k.gems || []).some(x => x.id === g.id));
    if (!ok) log(`  ${g.type}: dragged, but not in ${to.slot} after it`);
    return ok;
  }
  await sleep(350);
  for (let i = 0; i < 4; i++) {
    await tryPost('/hero/interact', { id: g.id });
    if (await waitGone(g.id, g.type, 1.2)) break;
  }
  const hero = await get('/hero');
  if (!hero.holding || hero.holding.id !== g.id) { log(`  ${g.type}: out, but not picked up again - left to the loot`); return false; }
  const r = await tryPost('/hero/equip', { slot: to.slot, index: to.index });
  if (r.error || (r.refused && r.refused.length)) { log(`  refused: ${r.error || r.refused.join('; ')} - the choice for a held essence instead`); await sortHands(1); return false; }
  return true;
}

// Iteration 25: a better socket for an essence that cannot fire where it is - a memory where it is live got a free
// socket (a new memory, a slot added), or an essence that gives it what it misses sits in a memory with room
// (planMove) - or its partner moved next to it (planPair). Asked when the loadout changed since the last look; at
// most two moves a look.
let rehomeSeen = '', rehomeSnap = null;
const VALUE_EPOCH = 180000;
let useSaid = -1;
// Iteration 29: what each worn memory does a minute, as the values are figured (memUse - iteration 40: the fair reference),
// and DevTools' raw counts.
function useLine(slots, hero) {
  const use = memUse(slots, hero);
  return slots.map(s => { const u = use.get(s.slot); const d = s.use; return !u ? s.slot : `${s.slot} ${String(s.memory).replace(/^St_/, '')} ${r2(u.c)}c ${r2(u.h)}h ${r2(u.D)}d${u.row.landSeen != null ? ` x${u.row.land.toFixed(2)} landed` : ''}` +
    (d ? ` [seen ${d.casts} casts ${d.hits} hits ${d.damage} dmg ${d.targetsPerCast}/cast in ${d.combatSeconds}s]` : ''); }).join(', ') + ' (fair, lvl 1)';
}
// Iteration 40: the loadout as the essences' plan depends on it - the memories, their levels and sockets, the essences with
// their quality and verdicts - and what changed since the last look, for the log.
const loadoutOf = hero => ((hero && hero.skills) || []).filter(k => k.type && k.sockets > 0 && ['Q', 'W', 'E', 'R'].includes(k.slot))
  .map(k => ({ slot: k.slot, type: k.type, level: k.level || 1, sockets: k.sockets, gems: (k.gems || []).filter(g => g.type).map(g => ({ id: g.id, type: g.type, q: g.quality, v: fitVerdict(g), index: g.index })) }));
const loadoutSig = lo => lo.map(k => `${k.slot}:${k.type}:${k.level}:${k.sockets}:${k.gems.map(g => `${g.id}/${g.q}/${g.v}`).join(',')}`).join('|');
function loadoutChange(a, b) {
  if (!a) return 'the first look';
  const out = [], by = (l, s) => l.find(k => k.slot === s);
  for (const k of b) {
    const o = by(a, k.slot), nm = String(k.type).replace(/^St_[A-Z]+_/, '');
    if (!o || o.type !== k.type) { out.push(`${k.slot} ${nm} equipped`); continue; }
    if (o.level !== k.level) out.push(`${k.slot} ${nm} level ${o.level} -> ${k.level}`);
    if (o.sockets !== k.sockets) out.push(`${k.slot} sockets ${o.sockets} -> ${k.sockets}`);
    for (const g of k.gems) {
      const og = a.flatMap(x => x.gems).find(x => x.id === g.id), gn = gemKey(g.type).replace(/^[A-Z]_/, '');
      const was = a.find(x => x.gems.some(y => y.id === g.id));
      if (!og) out.push(`${gn} socketed in ${k.slot}`);
      else if (was && was.slot !== k.slot) out.push(`${gn} moved ${was.slot} -> ${k.slot}`);
      else if (og.q !== g.q) out.push(`${gn} quality ${og.q} -> ${g.q}`);
    }
  }
  return out.length ? out.slice(0, 4).join(', ') : 'a verdict changed';
}
// The best prefix of the planned moves within MOVE.CAP (a move out of the way pays only with the one after it).
function movePrefix(moves, cap = MOVE.CAP) {
  let best = 0, bestK = 0, acc = 0;
  for (let k = 1; k <= Math.min(cap, moves.length); k++) { acc += moves[k - 1].gain; if (acc > best + 1e-9) { best = acc; bestK = k; } }
  return moves.slice(0, bestK);
}
// Iteration 25: a Dead essence to where it fires (planMove) or its partner beside it (planPair); iteration 40: then the whole
// assignment re-planned (planAll) after every loadout change - a memory equipped or replaced, a level, an essence's quality,
// a new essence - and the moves that pay done, the best first, MOVE.CAP a stop. Iteration 29's "once a run each, looked at
// every 3 minutes" is gone: the plan is looked at when what it rests on changes, and the move cost keeps it from churning.
async function rehomeEssences() {
  if (moveFails >= 2 || fitMissing) return;
  let hero = await get('/hero');
  if (hero.holding) return;
  const lo = loadoutOf(hero), sig = loadoutSig(lo);
  if (sig === rehomeSeen) return;
  const event = loadoutChange(rehomeSnap, lo);
  rehomeSeen = sig; rehomeSnap = lo;
  if (!lo.some(k => k.sockets > k.gems.length) || !lo.some(k => k.gems.length)) return;
  // Each socketed essence's verdicts in every memory (its /hero/fit by id), once for this look.
  const fits = new Map();
  for (const k of lo) for (const g of k.gems) if (g.id) { const f = await heroFit({ id: g.id }); if (f) fits.set(g.id, f); }
  const anyFit = [...fits.values()][0] || null;
  if (!anyFit) return;
  const ep = Math.floor(Date.now() / VALUE_EPOCH);
  if (useSaid !== ep) { useSaid = ep; log(`  memory use a minute: ${useLine(anyFit.slots, hero)}`); }
  let done = 0;
  // Dead ones first: to where they fire, or their partner beside them (as before).
  for (const k of lo) for (const g of k.gems.filter(x => x.v === 'Dead')) {
    if (done >= 2) break;
    const gm = { slot: k.slot, index: g.index, id: g.id, type: g.type, fit: (hero.skills.find(x => x.slot === k.slot).gems || []).find(x => x.id === g.id)?.fit };
    const move = planMove(hero, fits.get(g.id), gm);
    if (!move) continue;
    if (!(await moveEssence(move.gem, move.to, move.why))) return;
    done++; hero = await get('/hero');
  }
  if (!done) {
    for (const c of planPair(hero, anyFit)) {
      if (c.ask) {
        const f = fits.get(c.gem.id);
        const there = f && f.slots.find(s => s.slot === c.to.slot);
        if (!there || fitVerdict(there) === 'Dead') continue;
      }
      if (!(await moveEssence(c.gem, c.to, c.why))) return;
      done++; hero = await get('/hero');
      break;
    }
  }
  if (done) { rehomeSeen = ''; return; }   // the verdicts changed with the move: the plan at the next look
  const p = planAll(hero, fits);
  log(planLine(p, event));
  if (!p) return;
  for (const m of movePrefix(p.moves)) {
    const k = (hero.skills || []).find(x => x.slot === m.to);
    const s = { slot: m.to, sockets: k && k.sockets, free: 0 };
    const to = { slot: m.to, index: freeIndex(hero, s) };
    const why = `the plan: ${m.gain >= MOVE.LIVE / 2 ? 'it fires there' : `${m.gain >= 0 ? '+' : ''}${Math.round(m.gain)} dpm`} - ${valueLine(m.rv, p.use, p.slots)}`;
    if (!(await moveEssence(m.gem, to, why))) { hero = await get('/hero'); rehomeSnap = loadoutOf(hero); rehomeSeen = ''; return; }   // looked at again next time
    hero = await get('/hero');
  }
  rehomeSeen = loadoutSig(loadoutOf(hero)); rehomeSnap = loadoutOf(hero);
}

async function sortHands(depth = 0) {
  const hero = await get('/hero');
  if (!hero.holding) return;
  const item0 = hero.holding.item || {};
  if (COSTS_HEALTH.has(item0.type)) {
    log('dismantle', item0.type, '- it costs health on every cast');
    await tryPost('/hero/drop_held');
    await sleep(400);
    await tryPost('/hero/dismantle', { id: hero.holding.id });
    return;
  }
  if (item0.quality !== undefined) {
    // An essence (iteration 25): where it fires or wakes one beside it (pairing), else kept in a free socket for
    // later (parked - never R), else in place of a weaker parked one; dust only with every socket taken (planHeld).
    const fit = await heroFit();
    const p = planHeld(hero, fit, item0);
    const tag = `(${p.memory}: ${p.verdict || 'no verdict'}; ${p.why}${p.woken && p.woken.length ? ' - wakes ' + p.woken.join(', ') : ''}${p.value != null ? `; worth ${p.value.toFixed(2)}` : ''})`;
    // Iteration 29: why there - the held essence's value in each memory and what each memory does a minute.
    const vline = p.slots ? ' | ' + valueLine(p.rv, p.use, p.slots) : '';
    if (p.act === 'socket') {
      log('socket held', item0.type, '->', p.slot, p.index, `${tag} | ${fitLine(fit)}${vline}`);
      await tryPost('/hero/equip', { slot: p.slot, index: p.index });
      return;
    }
    if (p.act === 'replace') {
      // A click on a taken socket with an essence in hand: the one there comes out onto the ground, this one goes in.
      log('socket held', item0.type, '->', p.slot, p.index, `in place of ${p.out.type} (dead there) ${tag} | ${fitLine(fit)}${vline}`);
      await tryPost('/hero/equip', { slot: p.slot, index: p.index });
      await sleep(400);
      if (depth < 3) await takeBack(p.out, depth + 1);
      return;
    }
    log('dismantle', item0.type, `- ${p.why === 'harm' ? 'dead everywhere, only its drawback left' : p.why === 'only R free' ? 'dead everywhere, and only R (kept for essences that fit it) has a free socket' : 'every socket taken, nothing weaker parked'} | ${fitLine(fit)}`);
    await tryPost('/hero/drop_held');
    await sleep(400);
    await tryPost('/hero/dismantle', { id: hero.holding.id });
    return;
  }
  const empty = hero.skills.find(s => ['Q', 'W', 'E', 'R'].includes(s.slot) && !s.type);
  if (empty && hero.holding.item && hero.holding.item.level !== undefined) {
    log('equip held', hero.holding.item.type, '->', empty.slot);
    await tryPost('/hero/equip', { slot: empty.slot });
    return;
  }
  // Replace the weakest non-hero memory if the new one is better, else put it down.
  const own = hero.skills.filter(s => ['W', 'E'].includes(s.slot) && s.type && s.rarity !== 'Character');
  const rank = { Common: 0, Rare: 1, Epic: 2, Legendary: 3 };
  const item = hero.holding.item || {};
  // Iteration 37: "better" by impact (memoryWanted: what the worn W/E memories do a minute in fights - /hero/use - against
  // what the new one's numbers promise at its level), the least impactful one replaced; until iteration 36 by rarity. A
  // memory the prior does not know: the old rule (rarer than the rarest-least W/E one).
  if (item.type && item.level !== undefined) await learnLock(hero.holding.id, item.type);   // its channel, for its impact and heavy()
  const want = item.type ? memoryWanted(hero, await impactRows(hero),
    { type: item.type, level: item.level, chan: (skillLocks.get(item.type) || {}).lock }) : { none: true, unknown: true, why: 'no type' };
  let weakest = null;
  // Iteration 47: a memory this room's own replace put down is never worn again here - it is dismantled below.
  const putDown = item.type && droppedHere.has(item.type);
  if (putDown) log(`  memory ${item.type}: put down in this room for another - not taken back`);
  else if (want.unknown) {
    const w = own.sort((a, b) => (rank[a.rarity] ?? 0) - (rank[b.rarity] ?? 0) || a.level - b.level)[0];
    if (w && (rank[item.rarity] ?? 0) > (rank[w.rarity] ?? 0)) weakest = w;
    log(`  memory ${item.type} (${item.rarity} lvl ${item.level}): not in the numbers - by rarity${weakest ? `, rarer than ${weakest.slot} ${weakest.type}` : ', not rarer'}`);
  } else {
    if (!want.none) weakest = own.find(s => s.slot === want.slot) || null;
    log(`  memory ${item.type} (${item.rarity} lvl ${item.level}): ${want.why}`);
  }
  if (weakest && CHARGES.test(item.type || '')) log(`  ${item.type}: a charge to the next wall (over lava too) - not taken for ${weakest.type}`);
  // Iteration 19: a memory whose cast holds the hero (heavy - run-027's Doomsday Meteor, which replaced Annoying
  // Banner and froze the hero 1.4-2.4 s a cast next to Skoll) is not taken in place of one that does not: fight()
  // would cast it only with nobody near - a slot lost.
  let heavyHeld = false;
  if (weakest && item.type) {
    await learnLock(hero.holding.id, item.type);
    heavyHeld = heavy(item.type) && !heavy(weakest.type);
    if (heavyHeld) log(`  ${item.type}: a cast holds the hero ${(skillLocks.get(item.type) || {}).lock ?? '?'}s${KNOWN_HEAVY.has(item.type) ? ' (seen in run-027)' : ''} - not taken for ${weakest.type}`);
  }
  if (weakest && !CHARGES.test(item.type || '') && !heavyHeld) {
    // Iteration 25: the essences stay with the slot (HeroSkill.UnequipSkill/EquipSkill re-parent a slot's essences to
    // the memory put there) - logged before and after, since run-039's orchestrator saw E's two go with Dark Bolt.
    const before = (weakest.gems || []).filter(g => g.type).map(g => g.type);
    droppedHere.add(weakest.type);
    log('replace', weakest.slot, weakest.type, 'with', item.type, before.length ? `- its essences (${before.join(', ')}) stay in the slot` : '');
    await tryPost('/hero/equip', { slot: weakest.slot });
    if (before.length) {
      await sleep(400);
      const k = ((await get('/hero')).skills || []).find(s => s.slot === weakest.slot);
      const now = ((k && k.gems) || []).filter(g => g.type);
      const lost = before.filter(t => !now.some(g => g.type === t));
      log(`  ${weakest.slot} ${k && k.type}: essences now ${now.map(g => `${g.type} ${fitVerdict(g) || '?'}`).join(', ') || 'none'}${lost.length ? ` - LOST with the old memory: ${lost.join(', ')}` : ''}`);
    }
  } else {
    // Not wanted: put it down and dismantle it (G), as a player would - it is worth dream dust,
    // and the map refuses to travel while a memory is left lying about.
    log('dismantle', item.type);
    await tryPost('/hero/drop_held');
    await sleep(400);
    await tryPost('/hero/dismantle', { id: hero.holding.id });
  }
}

const RANK = { Common: 0, Rare: 1, Epic: 2, Legendary: 3, Unique: 3 };

// Treasures that heal, sold by the souvenir merchant (Smoothie, a room modifier; Jonas sells only
// memories and essences), and the share of max health below which one is bought. Decompiled:
// Treasure_SparklingElixir heals 40% of max health at once (any excess as a shield);
// Treasure_TokenOfGuidance heals 40% of the missing health after each of the next 4 room travels
// (TempEffect riftTravelCount 4). Base price 100 gold. Before iter-3 the shop bought only memories
// and essences and there was no way to heal at all.
const HEAL_TREASURES = { Treasure_SparklingElixir: 0.65, Treasure_TokenOfGuidance: 0.75 };
// Iteration 50: from the cycle's last zone (Despair, zone 3) on, gold buys nothing that matters before Primus (the Primus
// start room's Unmanned Shop sells memories and essences - run-054's 370 went on an Essence of Control that changed no plan),
// and nothing heals between Azurak and Primus (Guidance and regen orbs are dust under the Sparkling Dream Flask; the fights'
// heals - kills, basic hits - end with Azurak): a heal treasure is bought at any hurt there. The Elixir's excess becomes a
// shield for good (x0.5 under Grievous Wounds); the Token heals 40% of the missing health after each of the next 4 travels -
// Azurak's room, the rift, Primus's two rooms. Smoothie's stocks rarely hold one (none in runs 043-054's four opens).
const HEAL_LATE_ZONE = 3, HEAL_LATE = { Treasure_SparklingElixir: 1.01, Treasure_TokenOfGuidance: 1.01 };
const healTreasures = zone => typeof zone === 'number' && zone >= HEAL_LATE_ZONE ? HEAL_LATE : HEAL_TREASURES;

// Buy what makes the hero stronger: a memory better than the weakest one worn (or one for an
// empty slot), an essence while there are free sockets. Best first, while the gold lasts.
async function shop(m) {
  const HT = healTreasures(lastRoom && lastRoom.zoneIndex);   // iteration 50
  await tryPost('/hero/interact', { id: m.id });
  let open = false;
  const t = Date.now();
  while (Date.now() - t < 4000 + m.distance * 500 && !open) {
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    open = st.floatingWindow && st.floatingWindow.target && st.floatingWindow.target.id === m.id;
    if (!open) await sleep(250);
  }
  if (!open) {
    // Iteration 38: why (run-047's Smoothie: 6 s at 3.9 m, in a lull before the room's clear - opened after it in 1 s), and
    // not tried again this room (merchantCall).
    const [st, me] = await Promise.all([get('/state').catch(() => null),
      get('/interactables', { radius: 80 }).then(r => r.interactables.find(x => x.id === m.id)).catch(() => null)]);
    const memo = merchantMemo.get(m.id) || { seen: false, fails: 0, wantGold: Infinity, heals: [] };
    memo.fails++; merchantMemo.set(m.id, memo);
    const fw = st && st.floatingWindow;
    log(`  the shop did not open (${((Date.now() - t) / 1000).toFixed(1)}s; the hero @${me ? me.distance : '?'}m from it, canInteract ${me ? me.canInteract : '?'}; ` +
      `window ${fw ? JSON.stringify((fw.target && (fw.target.type || fw.target.id)) || fw).slice(0, 60) : 'none'}, ui ${st && st.uiState}, edit ${st && st.edit ? st.edit.mode : 'none'}, ` +
      `in combat ${st && st.hero ? st.hero.inCombat : '?'}, room cleared ${st && st.room ? st.room.exitOpen : '?'}) - not tried again this room`);
    return;
  }
  for (let round = 0; round < 6; round++) {
    const me = (await get('/interactables', { radius: 80 })).interactables.find(x => x.id === m.id);
    const stock = (me && me.details && me.details.stock) || [];
    const hero = await get('/hero');
    const worn = hero.skills.filter(k => ['Q', 'W', 'E', 'R'].includes(k.slot));
    const emptySlot = worn.some(k => !k.type);
    const replaceable = worn.filter(k => k.type && k.rarity !== 'Character');
    const weakest = replaceable.length ? Math.min(...replaceable.map(k => RANK[k.rarity] ?? 0)) : 99;
    const freeSockets = worn.filter(k => k.type).reduce((n, k) => n + Math.max(0, k.sockets - k.gems.length), 0);
    // Iteration 23: an essence only if it can fire in a worn memory with a free socket (/hero/fit by type).
    // Iteration 25 (offerClass): or if it wakes a Dead one in a memory with room ('pair'), or fires in place of a Dead
    // one ('swap'). Gold is not spent on one that would only be kept for later ('keep').
    const gemFit = new Map();
    for (const x of stock.filter(x => x.type === 'Gem' && x.count > 0 && x.item && !gemFit.has(x.item))) gemFit.set(x.item, offerClass(await heroFit({ type: x.item }), hero));
    const GEM_BUY = { free: 5, pair: 6, swap: 4 };
    const deadGems = [...gemFit].filter(([, c]) => c && !GEM_BUY[c]).map(([t, c]) => `${t} (${c === 'dead' || c === 'keep' ? 'dead in every worn memory' : 'live only where the sockets are taken'})`);
    if (deadGems.length && round === 0) log(`  not buying: ${deadGems.join(', ')}`);
    // Iteration 37: a memory is bought only when sortHands would take it - by impact (memoryWanted), not by rarity; and
    // nothing is bought with dream dust but health when hurt: the dust buys upgrades (the wells' best gain per dust).
    const impRows = stock.some(x => x.type === 'Skill' && x.count > 0) && !emptySlot ? await impactRows(hero) : [];
    const dustLeft = [];
    // Iteration 38: valued over the whole stock (the gold aside), remembered (stockMemo) - the merchant is not opened again
    // this room unless the gold now buys something it showed; what the gold buys now is the wants.
    const valued = stock.filter(x => x.count > 0 && x.price && !x.price.stardust)
      .map(x => {
        const r = RANK[x.rarity] ?? 0;
        let value = -1;
        if (x.type === 'Skill' && !COSTS_HEALTH.has(x.item) && !heavy(x.item) && !CHARGES.test(x.item || '')) {
          const w = emptySlot ? null : memoryWanted(hero, impRows, { type: x.item, level: x.level, chan: (skillLocks.get(x.item) || {}).lock });
          if (emptySlot || (w.unknown ? r > weakest : !w.none)) value = 10 + r * 3 + x.level;
          if (round === 0 && w && !w.unknown) log(`  offer ${x.item} (${x.rarity} lvl ${x.level}): ${w.why}`);
        }
        const gc = gemFit.get(x.item);
        if (x.type === 'Gem' && !COSTS_HEALTH.has(x.item) && (GEM_BUY[gc] || (gc == null && freeSockets > 0))) value = (GEM_BUY[gc] || 5) + r * 3 + x.level / 100;
        // Health, before anything else, when the hero is hurt (HT).
        const heal = x.type === 'Treasure' && HT[x.item];
        if (heal && hero.hp / hero.maxHp < heal) value = 50;
        if (value > 0 && value < 50 && (x.price.dreamDust || 0) > 0) { dustLeft.push(`${x.item}/${x.price.dreamDust} dust`); value = -1; }
        return { x, value };
      })
      .filter(w => w.value > 0)
      .sort((a, b) => b.value - a.value);
    if (dustLeft.length && round === 0) log(`  not buying with dream dust: ${dustLeft.join(', ')} - the dust goes to upgrades`);
    const wants = valued.filter(w => (w.x.price.gold || 0) <= hero.gold && (w.x.price.dreamDust || 0) <= hero.dreamDust);
    const memo = stockMemo(stock, valued, HT);
    merchantMemo.set(m.id, memo);
    if (!wants.length) {
      // Not again this room until the gold buys something it showed (merchantCall). A hurt hero and no heal in the stock is
      // said so; the souvenirs are stardust, the other treasures no heal (iteration 38).
      const hurtFor = Math.max(...Object.values(HT)), hurt = hero.hp / hero.maxHp < hurtFor;
      if (round === 0) log(`  nothing to buy with ${hero.gold} gold at ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp: ${stockLine(stock, HT)}` +
        ` - ${memo.wantGold < Infinity ? `wanted from ${memo.wantGold} gold` : 'nothing in it wanted'}${hurt && !memo.heals.length && /Smoothie/.test(m.type || '') ? '; hurt, but no heal in it (Sparkling Elixir, Token of Guidance)' : ''}`);
      break;
    }
    const buy = wants[0].x;
    log(`buy ${buy.name || buy.item} (${buy.rarity} ${buy.type}) for ${JSON.stringify(buy.price)} - have ${hero.gold} gold${HT[buy.item] ? ` at ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp: ${healLine(buy.item, hero, lucidActive)}` : ''}`);
    const r = await tryPost('/merchant/buy', { id: m.id, index: buy.index });
    if (r.error || (r.refused && r.refused.length)) { log('  refused:', r.error || r.refused.join('; ')); break; }
    await sleep(700);
    // What was bought lands at the hero's feet or in hand; put it on before choosing the next.
    await sortHands();
    for (const it of groundItems((await get('/interactables', { radius: 12 })).interactables)) {
      await pickUpBought(it); await sortHands();
    }
  }
}

// What was just bought, from the ground at the hero's feet. Every purchase in run-001..010 took 8-9 s
// from "buy" to the loot's "interact essence ... @0.4m" (20 of 20: 00:03:55 -> 00:04:04, 02:42:47 ->
// 02:42:56, 02:47:06 -> 02:47:15 ...): shop()'s one interact did not take (the item still dropping,
// or the shop window in the way - /hero/interact answers canInteractNow), waitGone sat out its 8 s,
// and the loot's next pass picked it up at once. Now the interact is repeated every ~1.2 s, 4.8 s at
// most; what is still there then is left to the loot as before.
async function pickUpBought(it) {
  const t = Date.now();
  const said = [];
  for (let i = 0; i < 4; i++) {
    const r = await tryPost('/hero/interact', { id: it.id });
    said.push(r && r.error ? 'error' : r && r.canInteractNow === false ? 'no' : 'yes');
    if (await waitGone(it.id, it.type, 1.2)) {
      if (i) log(`  ${it.name || it.type}: picked up on try ${i + 1} (${((Date.now() - t) / 1000).toFixed(1)}s; canInteractNow ${said.join(',')})`);
      return true;
    }
  }
  log(`  ${it.name || it.type}: not picked up in the shop (${((Date.now() - t) / 1000).toFixed(1)}s; canInteractNow ${said.join(',')}) - left to the loot`);
  return false;
}

// Iteration 16: a dash on a long walk with no enemy about. The dash (Ai_GenericDodge, decompiled: a
// displacement to the aimed point at `speed`, 12 m/s by default) covers ~5.5 m in ~0.5 s against ~1.1 s
// on foot, and its charges come back one at a time (~3.3 s each). Taken only where the walk after it is
// long enough for the charge to be back before it is wanted: `minLeft` metres of path left after the dash
// (the boss-room approach: 28 m - the intro starts 13-15 m short of the arena's centre, so ~3 s of walk
// and the cutscene remain). Along the first leg of /nav/path's path (a dash is a straight line and stops at
// a wall), never where the path is incomplete or crosses hazard ground, never in LavaLand.
const WALK_DASH = 5.5, QUIET_DASH_LEFT = 28, LOOT_DASH_LEFT = 3;
// Iteration 17: off. run-024 (the first run with them, 12 dashes): each dash covered ~5 m in ~0.3 s and was followed by
// ~0.6 s standing (and a slowdown before it) - the walks with dashes averaged 5.04-5.27 m/s against 5.20 plain, so
// nothing gained - and Belphomet's fight opened with 1 of 2 charges after the 3 dashes of the boss-room approach.
// Kept for a try with the next move sent at once (the stand is likely the 0.52 s sleep and the cast's own stop).
const WALK_DASHES = false;
const walkDashes = { n: 0, at: 0 };
async function walkDash(goal, hero, minLeft, what) {
  if (!goal || onLavaZone() || Date.now() - walkDashes.at < 600) return false;
  const mv = hero && (hero.skills || []).find(k => k.slot === 'Movement');
  if (!mv || !mv.type || !mv.trigger || !mv.trigger.canCast) return false;
  // Iteration 30: only with both charges ready - the fight the walk leads into opens with one in hand at the least
  // (run-024: Belphomet's fight opened with 1 of 2 after the approach's dashes). Off anyway (WALK_DASHES).
  if (mv.trigger.charges != null && mv.trigger.charges < 2) return false;
  if (hero.position && dist(hero.position, goal) < minLeft + WALK_DASH - 1) return false;   // too short even as the crow flies
  const p = await get('/nav/path', { x: goal.x, z: goal.z }).catch(() => null);
  if (!p || p.status !== 'PathComplete' || !Array.isArray(p.legs) || !p.legs.length || p.onHazard > 0) return false;
  if (p.length - WALK_DASH < minLeft) return false;
  const leg = p.legs[0], L = leg.length;
  if (!(L >= 4)) return false;   // a corner right ahead: the straight dash would cut into its wall
  const k = Math.min(WALK_DASH, L) / L;
  const at = { x: leg.from.x + (leg.to.x - leg.from.x) * k, z: leg.from.z + (leg.to.z - leg.from.z) * k };
  // Iteration 28: not onto red - a strike or telegraph still to land, a lobbed shot's landing (walkReds), a pool.
  const th = await get('/threats', { radius: 25 }).catch(() => null);
  if (th && walkReds(th).concat(poolAreas(pools, Date.now())).some(a => areaDepth(at, a, 0.4) > 0)) return false;
  const r = await tryPost('/hero/cast', { slot: 'Movement', x: at.x, z: at.z, move: false });
  if (r.error) return false;
  walkDashes.at = Date.now(); walkDashes.n++;
  await sleep(Math.round(Math.min(WALK_DASH, L) / 12 * 1000) + 60);   // the displacement over before the next order
  log(`  dash on the walk to ${what} (${p.length.toFixed(0)}m of path, ${(Math.min(WALK_DASH, L)).toFixed(1)}m of it dashed)`);
  return true;
}

async function breakProp(p) {
  await tryPost('/hero/attack', { target: p.id });
  for (let i = 0; i < 40; i++) {
    try { if (!(await get('/reflect/get', { path: '#' + p.id + '.isAlive' }))) break; } catch { break; }
    // Dead, lava, enemies (enemyNear); or burning where it stands (a fire beside the deposit - run-012).
    if (i % 2 === 1 && (await enemyNear() || pools.some(q => q.fixed && dist(q.centre, p.position) < q.radius + 1))) break;
    await sleep(250);
  }
}

const groundItems = list => list.filter(i => (i.kind === 'memory' || i.kind === 'essence') && i.details && i.details.onGround && !i.details.lockedForMe);

// Use a shrine and wait for what it gives: the walk there, the use, the item falling out.
async function useShrine(it) {
  const before = new Set(groundItems((await get('/interactables', { radius: 80 })).interactables).map(i => i.id));
  const t = Date.now();
  const limit = 4000 + it.distance * 500;
  // An offer taken (Chaos, Corrupted Chaos: a stat, an upgrade, a slot) drops nothing: 51 Chaos and 3
  // Corrupted Chaos uses in runs 001-020, never an item after one - but the drop wait below sat out its
  // full 4 s each time (run-020: ~5 s from the offer to the next thing, three times). Iteration 15.
  let offered = false;
  while (Date.now() - t < limit) {
    if (await enemyNear()) return;
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    const now = (await get('/interactables', { radius: 80 })).interactables.find(x => x.id === it.id);
    const choices = now && now.details && Array.isArray(now.details.choices) ? now.details.choices : null;
    const offers = now && now.details && now.details.offers && Array.isArray(now.details.offers.items) ? now.details.offers.items : null;
    const windowOpen = st.floatingWindow && st.floatingWindow.target && st.floatingWindow.target.id === it.id;
    if (!choices && offers && offers.length && windowOpen) {
      // Rank by any rarity or level the offer carries; the first when nothing tells them apart.
      const R = { Common: 0, Rare: 1, Epic: 2, Legendary: 3 };
      const score = o => {
        const t = JSON.stringify(o.offer);
        let r = 0;
        for (const [k, v] of Object.entries(R)) if (t.includes('"' + k + '"')) r = Math.max(r, v);
        const m = t.match(/_(C|R|E|L)_/);
        if (m) r = Math.max(r, { C: 0, R: 1, E: 2, L: 3 }[m[1]]);
        const lv = t.match(/"(level|quality)":\s*(\d+)/);
        // Corrupted chaos offers are names: a slot is worth more than any stat.
        const named = /AddedEssenceSlot|AddedSkillSlot|Slot/.test(t) ? 50 : 0;
        // At the same rarity, health first: zone 1 is where runs die (1 clear in 5), and run-006 took
        // +8 armour over +120 max health (Rare, Chaos, LavaLand) and reached Infernus at 110/696.
        const stat = /"type":"MaxHealth"/.test(t) ? 3 : /"type":"Armor"/.test(t) ? 1 : 0;
        return named + r * 10 + stat + (lv ? +lv[2] / 100 : 0);
      };
      const pick = offers.slice().sort((x, y) => score(y) - score(x))[0];
      log('  offers:', offers.map(o => JSON.stringify(o.offer).slice(0, 80)).join(' | '), '-> take', pick.index);
      const r = await tryPost('/shrine/choose', { id: it.id, index: pick.index });
      if (r.error || (r.refused && r.refused.length)) log('  refused:', r.error || r.refused.join('; '));
      else offered = true;
      await sleep(800);
      break;
    }

    if (choices && choices.length && windowOpen) {
      // The highest level, never one that costs health (unless that is all there is).
      const ok = i => !COSTS_HEALTH.has(choices[i].type);
      // Iteration 23: an essence that can fire in a worn memory with a free socket before one that cannot
      // (/hero/fit by type); a dead one only when nothing else is offered (it is dismantled for dust then).
      // Iteration 25 (offerClass): one that fires or wakes a Dead one beside it, then one that fires in place of a Dead
      // one, then one kept for later; dust last.
      const cls = [];
      const heroNow = await get('/hero');
      for (const c of choices) cls.push(/^Gem_/.test(c.type || '') ? offerClass(await heroFit({ type: c.type }), heroNow) : null);
      const fitScore = i => ({ dead: 0, keep: 1, full: 1, swap: 2, free: 3, pair: 3 })[cls[i]] ?? 3;
      let best = choices.findIndex((c, i) => ok(i));
      if (best < 0) best = 0;
      choices.forEach((c, i) => { if (ok(i) && (fitScore(i) > fitScore(best) || (fitScore(i) === fitScore(best) && (c.level || 0) > (choices[best].level || 0)))) best = i; });
      log('  choices:', choices.map((c, i) => `${c.name || c.type} +${c.level - 1}${cls[i] ? ` (${cls[i]})` : ''}`).join(', '), '-> take', best);
      const r = await tryPost('/shrine/choose', { id: it.id, index: best });
      if (r.error || (r.refused && r.refused.length)) { log('  refused:', r.error || r.refused.join('; ')); await sleep(400); continue; }
      await sleep(800);
      break;
    }
    if (choices && choices.length && !windowOpen && now.distance < 2.5 && Date.now() - t > 2500 && (Date.now() - t) % 2000 < 400) {
      // Standing at it with no window: use it again.
      await tryPost('/hero/interact', { id: it.id });
    }
    if (!now || !now.details || !now.details.shrine || !now.details.shrine.available) break;
    await sleep(300);
  }
  // What it drops lands a moment later.
  for (let i = 0; i < 16 && !offered; i++) {
    const fresh = groundItems((await get('/interactables', { radius: 80 })).interactables).filter(x => !before.has(x.id));
    if (fresh.length) { log('  it gave', fresh.map(f => f.name || f.type).join(', ')); break; }
    await sleep(250);
  }
  if ((await get('/state')).floatingWindow) await tryPost('/ui/click', { text: 'Close' });
}

let lootAbort = 0;   // > 0: stop looting when an enemy comes this close
// Every wait of the loot (the walks to things, shrine uses, pickups, the fire wait) asks this each
// ~250-500 ms. Besides enemies coming (lootAbort), since iter-10 it also stops a walk for:
//   - death: run-011 died walking to a shrine and the bot polled on for 35 s, tried to travel at
//     0/780 hp and ended "stuck" with no `dead` event (the death check was only on fight()'s result).
//     Now a knocked-out hero or the Result screen throws Dead, which auto() turns into the event.
//   - lava: run-011's /hero/interact walk to Pyrana's Love (63 m) went straight across ~25 m of
//     LavaLand lava, 608 -> 0 in 4.8 s (lava ticks every ~0.25 s, ramping 21 -> 30, the fire dot on
//     top). The first LavaLand_Lava hit stops the walk and takes the hero off it (leaveHazard); the
//     loot of that room ends there. (walkSafe keeps the walks off the lava to begin with.)
class Dead extends Error { constructor() { super('the hero is dead'); this.dead = true; } }
// Iteration 27: the Result screen after the final boss (Primus) is the victory, not a death (deadState, finalDown).
const isDead = st => deadState(st, !!finalDown);
let abortWhy = null;   // why enemyNear() last said stop: 'enemies' | 'lava' | 'danger' (iteration 48: abortDetail says what)
// Iteration 48: the lull's loot inside a fight room (fight(): the room not clear yet) also stops for a monster's blow taken
// (not a DoT's tick) and for red under the hero - run-053 died on a blind walk (despairHop's) in such a room.
let lootDanger = false, abortDetail = '';
async function enemyNear() {
  abortWhy = null;
  const st = await get('/state').catch(() => null);
  if (isDead(st)) throw new Dead();
  const hits = onLavaZone() || (lootAbort && lootDanger) ? await readHits() : [];
  if (onLavaZone()) {
    const lava = hits.filter(h => /^LavaLand_Lava/.test(h.by || ''));
    if (lava.length) { abortWhy = 'lava'; await leaveHazard(`${Math.round(lava.reduce((s, h) => s + h.amount, 0))} from lava`); return true; }
  }
  if (!lootAbort) return false;
  const [e, th] = await Promise.all([get('/entities', { kind: 'enemies', radius: lootAbort, limit: 1 }).then(r => r.entities),
    lootDanger ? get('/threats', { radius: HOP_CLEAR }).catch(() => null) : null]);
  if (e.length) { abortWhy = 'enemies'; return true; }
  if (lootDanger) {
    const blows = hits.filter(h => h && !h.overTime && (h.amount || 0) > 0 && /Mon_/.test(h.by || ''));
    const red = th && (th.areas || []).find(a => a && a.inside && a.shape !== 'safe' && a.shape !== 'zone');
    if (blows.length || red) {
      abortWhy = 'danger';
      abortDetail = [blows.length ? `took ${Math.round(blows.reduce((t, h) => t + h.amount, 0))} (${[...new Set(blows.map(h => h.caster || h.by))].join(', ')})` : '',
        red ? `in a ${red.shape} of ${red.by || red.type || '?'}` : ''].filter(Boolean).join(', ');
      return true;
    }
  }
  return false;
}
const abortLine = () => abortWhy === 'lava' ? '  lava under the hero - the loot of this room ends here' : abortWhy === 'danger' ? `  ${abortDetail} - the loot stops, back to the fight` : '  enemies close - back to the fight';

// ----- hazard ground (LavaLand's lava) ---------------------------------------------------------
// /hero/move and /hero/interact walk the navmesh's shortest path, and lava is navmesh (monsters walk
// it); /nav/grid leaves hazard cells out of `walk` and `reach` (the game's INotPlayableOnTop ground).
// So in a LavaLand room a walk of the loot or to the exit goes by the grid: the target's cell must be
// reachable over hazard-free cells, and the hero walks there in straight legs that stay on them
// (string-pulled from the grid's path). No such way: the target is left.
let zoneName = '';   // the room's zone (auto)
const onLavaZone = () => /LavaLand/i.test(zoneName);
const gridPos = (g, k) => ({ x: g.origin.x + (k % g.size) * g.step, z: g.origin.z + Math.floor(k / g.size) * g.step });
const gridCell = (g, p) => {
  const i = Math.round((p.x - g.origin.x) / g.step), j = Math.round((p.z - g.origin.z) / g.step);
  return i < 0 || j < 0 || i >= g.size || j >= g.size ? -1 : j * g.size + i;
};
// Whether the straight line a -> b stays on standable cells (sampled every half step).
function segmentClear(g, a, b) {
  const n = Math.max(1, Math.ceil(dist(a, b) / (g.step * 0.5)));
  for (let s = 0; s <= n; s++) {
    const k = gridCell(g, { x: a.x + (b.x - a.x) * s / n, z: a.z + (b.z - a.z) * s / n });
    if (k < 0 || g.walk[k] !== '.') return false;
  }
  return true;
}
// Waypoints from the hero to `target` over hazard-free ground, or null (pure; tests/iter10.test.mjs).
//   g: /nav/grid (walk: '.'/'#' per cell, reach: path length from the hero's cell, -1 unreachable)
function safePath(g, target, near = 3) {
  let t = -1;
  for (let k = 0; k < g.reach.length; k++) {
    if (g.reach[k] < 0 || dist(gridPos(g, k), target) > near) continue;
    if (t < 0 || g.reach[k] < g.reach[t]) t = k;
  }
  if (t < 0) return null;
  const cells = [t];
  for (let k = t; g.reach[k] > 0;) {
    const i = k % g.size, j = Math.floor(k / g.size);
    let next = -1;
    for (let dj = -1; dj <= 1 && next < 0; dj++) for (let di = -1; di <= 1; di++) {
      const ni = i + di, nj = j + dj;
      if (ni < 0 || nj < 0 || ni >= g.size || nj >= g.size) continue;
      const n = nj * g.size + ni;
      if (g.reach[n] === g.reach[k] - 1) { next = n; break; }
    }
    if (next < 0) break;
    cells.push(next); k = next;
  }
  const pts = cells.reverse().map(k => gridPos(g, k));   // hero's cell first
  const out = [];
  for (let i = 0; i < pts.length - 1;) {
    let j = pts.length - 1;
    while (j > i + 1 && !segmentClear(g, pts[i], pts[j])) j--;
    out.push(pts[j]); i = j;
  }
  return out.length ? out : [pts[pts.length - 1]];
}
// --- campfires (iteration 34) ---
// run-043's death (zone 2, Room_Ink_Shop_1, hp 534/696): the travel's /hero/interact on the exit - the game's own walk -
// took the hero onto the room's campfire, Forest_Fireplace, and it stood there 0.26 m from it for ~12 s (5.8 Fire a hit
// every ~0.2 s, and the burn it lights, Se_Elm_Fire, 17-20 a tick) until dead; travel() read no hits and logged "stuck".
// The same fire in run-041 (the same room, 14 on the way out), run-028 (zone 0's start, 2) and run-012 (dead, the loot).
// Decompiled (history/it27/contents.il): Forest_Fireplace is an Actor - not an Entity nor an interactable, so neither
// /entities nor /interactables list it - with `radius`; every damageInterval it hits each Hero, Monster and Summon inside
// OverlapCircleAllEntities(position, radius) for damageMaxHealthRatio of max health, Fire. So it is read at room entry
// (/reflect/find + its position and radius: pure reads) and kept as a fixed fire circle for the whole room - the fight's
// plan (poolAreas), the loot's walks and the exit's walk (walkSafe -> fireDetour) - before its first hit.
const FIREPLACE_TYPE = 'Forest_Fireplace';
// The circle kept round a read campfire (iteration 41): its real hit reach - its radius + the hero's body (FIREPLACE_BODY:
// OverlapCircleAllEntities is Physics2D.OverlapCircle on the entities' colliders, so a body that touches the circle is in it;
// the hits in /damage landed 0.26 m (run-043) and 0.46 m (run-012) from the centre, the radius read 0.15 in run-049) - and
// FIREPLACE_PAD more; FIREPLACE_MIN at least. Was the radius + 1 m, FIXED_R (3 m) at least: run-049's zone-1 shop had Jonas
// 1.6 m from its campfire - "in it, left", 175 gold carried off unspent. A fire whose radius was not read (learned from a
// hit): FIXED_R. `hit` (addFireplace) is the reach itself: an interact target within it is left; one near the circle is
// walked to from the side away from the fire (fireApproach).
const FIREPLACE_BODY = 0.35, FIREPLACE_PAD = 0.75, FIREPLACE_MIN = 1.25;
const fireplaceRadius = r => typeof r === 'number' && r > 0 ? Math.round(Math.max(FIREPLACE_MIN, r + FIREPLACE_BODY + FIREPLACE_PAD) * 100) / 100 : FIXED_R;
// A read campfire into the room's pools (notePools' shape), fixed for the room. Pure.
function addFireplace(pools, f, now) {
  if (!f || !f.centre) return;
  const radius = fireplaceRadius(f.radius), p = pools.find(q => dist(q.centre, f.centre) < 1.5);
  const hit = typeof f.radius === 'number' && f.radius > 0 ? f.radius + FIREPLACE_BODY : null;
  if (p) { p.radius = Math.max(p.radius, radius); p.until = Math.max(p.until, now + FIXED_LIFE); p.born = Math.min(p.born, now); p.fixed = true; p.by = p.by || FIREPLACE_TYPE; if (hit != null) p.hit = Math.max(p.hit || 0, hit); }
  else pools.push({ centre: { x: f.centre.x, z: f.centre.z }, radius, born: now, until: now + FIXED_LIFE, fixed: true, by: FIREPLACE_TYPE, ...(hit != null ? { hit } : {}) });
}
// An interact target beside a fire (within its circle + FIRE_NEAR): the place to walk to first - FIRE_APPROACH m past the
// target on the side away from the fire (the fire -> target line, then up to 60 deg either way), on a standable cell of `g`
// (when given) and out of every fire's circle; the game's interact walk from there goes on toward the target and stops at
// its reach, never nearer the fire than the target. null: no fire near, or no such place. The target within the fire's hit
// reach itself (+0.3): { inFire: fire }. Pure.
const FIRE_NEAR = 1.5, FIRE_APPROACH = 1.2;
function fireApproach(target, fires, g = null) {
  if (!target) return null;
  const core = fires.find(f => dist(target, f.centre) < (f.hit != null ? f.hit + 0.3 : f.radius - 1));
  if (core) return { inFire: core };
  const f = fires.filter(q => dist(target, q.centre) < q.radius + FIRE_NEAR).sort((a, b) => dist(target, a.centre) - dist(target, b.centre))[0];
  if (!f) return null;
  const d = dist(target, f.centre), u = d > 0.05 ? { x: (target.x - f.centre.x) / d, z: (target.z - f.centre.z) / d } : { x: 1, z: 0 };
  for (const deg of [0, 20, -20, 40, -40, 60, -60]) {
    const a = deg * Math.PI / 180, c = Math.cos(a), s = Math.sin(a);
    const v = { x: u.x * c - u.z * s, z: u.x * s + u.z * c };
    const p = { x: target.x + v.x * FIRE_APPROACH, z: target.z + v.z * FIRE_APPROACH };
    if (fires.some(q => dist(p, q.centre) < q.radius + 0.3)) continue;
    if (g && typeof g.walk === 'string') { const k = gridCell(g, p); if (k < 0 || g.walk[k] !== '.') continue; }
    return { p, fire: f, deg };
  }
  return null;
}
// The fixed fires burning now that a walk keeps off: campfires read or learned from a hit - not the Ink boss room's
// ground (10.5 m round the boss's arena, off once the room is clear; the soul lies in it).
const fireCircles = (pools, now) => pools.filter(p => p.fixed && p.born <= now && now < p.until && !GROUND_HIT.test(p.by || ''));
// The first fire whose circle (+ margin) the walk along pts (the hero first) passes, or null. Pure.
function fireOnWay(pts, fires, margin = 0.5) {
  if (pts.length === 1) return fires.find(f => dist(pts[0], f.centre) < f.radius + margin) || null;
  for (let i = 1; i < pts.length; i++) for (const f of fires) if (segDist(f.centre, pts[i - 1], pts[i]) < f.radius + margin) return f;
  return null;
}
// Waypoints from `me` to within `near` of `target` over the grid's standable cells, off every fire circle (+ margin), the
// last one with a straight line to the target clear of them; [] when there already; null when there is no such way (or
// the target is off the grid). Breadth first from the hero's cell (not the grid's `reach`, which knows no fires); from
// inside a circle only outward (each step less deep in it). String-pulled like safePath. Pure (tests/iter34.test.mjs).
function fireAvoidPath(g, me, target, fires, near = 2.5, margin = 0.6) {
  if (!g || typeof g.walk !== 'string' || !me || !target) return null;
  const N = g.size * g.size;
  const heat = p => { let h = -Infinity; for (const f of fires) h = Math.max(h, f.radius + margin - dist(p, f.centre)); return h; };
  const H = new Float64Array(N);
  for (let k = 0; k < N; k++) H[k] = heat(gridPos(g, k));
  const s = gridCell(g, me);
  if (s < 0 || gridCell(g, target) < 0) return null;
  const okFrom = (n, k) => g.walk[n] === '.' && (H[n] <= 0 || H[n] < H[k]);
  const goal = k => H[k] <= 0 && dist(gridPos(g, k), target) <= near && !fireOnWay([gridPos(g, k), target], fires, 0);
  const prev = new Int32Array(N).fill(-2);
  prev[s] = -1;
  const q = [s];
  let t = -1;
  for (let qi = 0; qi < q.length && t < 0; qi++) {
    const k = q[qi];
    if (goal(k)) { t = k; break; }
    const i = k % g.size, j = (k - i) / g.size;
    for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) {
      if (!di && !dj) continue;
      const ni = i + di, nj = j + dj;
      if (ni < 0 || nj < 0 || ni >= g.size || nj >= g.size) continue;
      const n = nj * g.size + ni;
      if (prev[n] !== -2 || !okFrom(n, k)) continue;
      if (di && dj && (g.walk[j * g.size + ni] !== '.' || g.walk[nj * g.size + i] !== '.')) continue;   // no corner cut
      prev[n] = k; q.push(n);
    }
  }
  if (t < 0) return null;
  if (t === s) return [];
  const cells = [];
  for (let k = t; k >= 0; k = prev[k]) cells.push(k);
  const pts = cells.reverse().map(k => gridPos(g, k));
  pts[0] = { x: me.x, z: me.z };
  // A straight leg a -> b: on standable cells, never deeper into a fire than where it starts (or out of them all).
  const clear = (a, b) => {
    const lim = Math.max(0, heat(a)), n = Math.max(1, Math.ceil(dist(a, b) / (g.step * 0.5)));
    for (let x = 1; x <= n; x++) {
      const p = { x: a.x + (b.x - a.x) * x / n, z: a.z + (b.z - a.z) * x / n }, k = gridCell(g, p);
      if (k < 0 || g.walk[k] !== '.' || heat(p) > lim + 1e-9) return false;
    }
    return true;
  };
  const out = [];
  for (let i = 0; i < pts.length - 1;) {
    let j = pts.length - 1;
    while (j > i + 1 && !clear(pts[i], pts[j])) j--;
    out.push(pts[j]); i = j;
  }
  return out;
}
// Out of a fire circle: a point `margin` past its edge, the nearest to the hero (the way toward `toward` breaks ties),
// on a standable cell when a grid is given. Pure.
function fireStepOut(me, fire, toward = null, g = null, margin = 1.2) {
  const R = fire.radius + margin;
  let best = null;
  for (let a = 0; a < 16; a++) {
    const ang = a * Math.PI / 8, p = { x: fire.centre.x + Math.cos(ang) * R, z: fire.centre.z + Math.sin(ang) * R };
    if (g && typeof g.walk === 'string') { const k = gridCell(g, p); if (k < 0 || g.walk[k] !== '.') continue; }
    const sc = dist(me, p) + (toward ? 0.3 * dist(p, toward) : 0);
    if (!best || sc < best.sc) best = { p, sc };
  }
  if (best) return best.p;
  const d = dist(me, fire.centre);
  const u = d > 0.1 ? { x: (me.x - fire.centre.x) / d, z: (me.z - fire.centre.z) / d } : { x: 1, z: 0 };
  return { x: fire.centre.x + u.x * R, z: fire.centre.z + u.z * R };
}
// Hit while walking to the exit (travel): where to step. Standing in a known pool or fire (notePools has just learned any
// fire the hits came from) - out of it; else a direct hit (not over time) from `from` within HIT_NEAR m - away from that
// point, as out of a fixed fire. null: nothing to step out of (a burn ticking on, a shot from afar). Pure.
const HIT_NEAR = 4;
function hitStep(me, hits, pools, now, toward = null) {
  for (const p of pools) if (p.born <= now && now < p.until && dist(me, p.centre) < p.radius) return { p: fireStepOut(me, p, toward), src: p.by || 'a burning pool', fire: p };
  const h = [...(hits || [])].reverse().find(x => x && x.from && !x.overTime && dist(me, x.from) < HIT_NEAR);
  if (!h) return null;
  const fire = { centre: { x: h.from.x, z: h.from.z }, radius: Math.max(FIXED_R, dist(me, h.from) + 0.5) };
  return { p: fireStepOut(me, fire, toward), src: h.by || '?', fire };
}
// --- end campfires ---
// Where to step off the lava: the nearest reachable cell at least 2 cells from any that cannot be stood
// on, else the grid's `hero` cell (the nearest standable one). run-017 node 11: three step-offs to the
// `hero` cell within a second, each ending at once, and 25 + 51 + 26 more from the lava - that cell was
// most likely the lava's very edge (the lava also rises and falls: LavaLand_Lava.enableTranslation, so
// its edge is not always where the grid saw it).
function offHazardCell(g) {
  const h = (g.size - 1) / 2, centre = { x: g.origin.x + h * g.step, z: g.origin.z + h * g.step };
  let best = -1, bd = Infinity;
  for (let k = 0; k < g.reach.length; k++) {
    if (g.reach[k] < 0 || g.clear[k] < 2) continue;
    const d = dist(gridPos(g, k), centre);
    if (d < bd) { bd = d; best = k; }
  }
  if (best >= 0 && bd <= 6) return gridPos(g, best);
  return g.hero ? gridPos(g, g.hero.j * g.size + g.hero.i) : null;
}
// Whether a lunge may land at `p` (LavaLand): its cell standable with room round it. Outside the grid: no.
function lungeOk(g, p) {
  if (!g || !(g.hazardCells > 0) || typeof g.walk !== 'string') return true;
  const k = gridCell(g, p);
  return k >= 0 && g.walk[k] === '.' && g.clear[k] >= 2;
}
// Whether the hero stood on hazard ground when the grid was read: the grid's centre is the hero's own
// position (/nav/grid centres on agentPosition, which is on the navmesh), so its cell is '#' only when
// the ground under it is a hazard (NavGrid.OnHazard - the same ray down LavaLand_Lava.IsEntityOnLava casts).
function gridOnHazard(g) {
  if (!g || !(g.hazardCells > 0) || typeof g.walk !== 'string') return false;
  const h = (g.size - 1) / 2;
  return g.walk[h * g.size + h] === '#';
}
// Off the lava in a fight (iteration 14; run-019 died standing in it, dash ready): the reachable cell
// nearest to `me` with room round it - 3 cells from anything unstandable counts 0.8 m better than 2 (the
// lava's edge moves) - and not next to an enemy nor in red (plan's areas). null when the grid has none (the
// caller reads a bigger one). Pure; tests/iter14.test.mjs.
function lavaExit(g, me, enemies = [], areas = []) {
  let best = null;
  const wet = wetCells(g);
  for (let k = 0; k < g.reach.length; k++) {
    if (g.reach[k] < 0 || g.clear[k] < 2 || wet(k)) continue;
    const p = gridPos(g, k);
    const d = dist(me, p);
    let score = d + (g.clear[k] === 2 ? 0.8 : 0);
    for (const e of enemies) if (dist(p, e.position) < 2.5) score += 3;
    for (const a of areas) if (areaDepth(p, a) > 0) score += 4;
    if (!best || score < best.score) best = { p, d, clear: g.clear[k], score };
  }
  return best;
}
// The dash across the lava (iteration 15; run-021 died boxed in by it): a dry cell 2.5-5.5 m off, with room
// (clear >= 2), not one the rising lava reaches, the straight line to it over dry ground or lava only (a dash
// stops where the navmesh ends - Dew.GetValidAgentDestination_LinearSweep - and lava is navmesh; a wall is
// not), the least red there by plan()'s measure (10 + 6 fill + 3 depth per area), not next to an enemy.
// The grid's `hazard` string (the iter-14 mod) tells lava ('L') from walls; null without it. `lava`: metres
// of it on the line (a dash is a displacement: the lava does not tick on it). Pure; tests/iter15.test.mjs.
function lavaHop(g, me, areas = [], enemies = []) {
  if (!g || typeof g.walk !== 'string' || typeof g.hazard !== 'string' || !(g.hazardCells > 0)) return null;
  const wet = wetCells(g);
  let best = null;
  for (let k = 0; k < g.walk.length; k++) {
    if (g.walk[k] !== '.' || !(g.clear[k] >= 2) || wet(k)) continue;
    const p = gridPos(g, k), md = dist(me, p);
    if (md < 2.5 || md > 5.5) continue;
    const n = Math.max(2, Math.ceil(md / (g.step * 0.5)));
    let ok = true, lava = 0;
    for (let s = 1; s < n && ok; s++) {
      const q = gridCell(g, { x: me.x + (p.x - me.x) * s / n, z: me.z + (p.z - me.z) * s / n });
      if (q < 0) ok = false;
      else if (g.hazard[q] === 'L') lava += md / n;
      else if (g.walk[q] !== '.') ok = false;
    }
    if (!ok) continue;
    let area = 0;
    for (const a of areas) { const dp = areaDepth(p, a); if (dp > 0) area += 10 + 6 * a.fill + 3 * dp; }
    if (area > 0 && knockLava(g, p, areas)) area += KNOCK_W;   // iteration 22
    let score = area - 0.3 * Math.min(g.clear[k], 4) + 0.2 * lava;
    for (const e of enemies) if (dist(p, e.position) < 2.5) score += 3;
    if (!best || score < best.score) best = { k, p, md, clear: g.clear[k], area, lava, score };
  }
  return best;
}
// The first stretch of lava along a walk (iteration 17): `pts` the navmesh path's corners, the hero first, read
// against the grid's `hazard` ('L' lava now). run-023's Combat_0_3 had a lava strip between its entrance and its
// exit; each of its three crossings on foot took 2-4 ticks (~20 each) and lit the burn (fire ~40-70 more). A dash
// takes none (LavaLand_Lava skips a displacing entity). Returns null (no lava on the part of the way the grid
// covers) or { from: the last dry point before it (0.5 m short; the hero's own spot when on it already), to: the
// first dry point past it with `margin`, start/end/len (metres along the path), onIt, dashLen, dashOk: the straight
// line from -> to over dry ground or lava only (a dash stops at a wall), within 5.5 m }. Pure.
function lavaRun(g, pts, margin = 0.8) {
  if (!g || typeof g.hazard !== 'string' || typeof g.walk !== 'string' || !pts || pts.length < 2) return null;
  const S = [];
  const kind = p => { const k = gridCell(g, p); return k < 0 ? '?' : g.hazard[k] === 'L' ? 'L' : g.walk[k] === '.' ? '.' : '#'; };
  let s = 0;
  for (let i = 1; i < pts.length; i++) {
    const a = pts[i - 1], b = pts[i], L = dist(a, b), n = Math.max(1, Math.ceil(L / 0.25));
    for (let t = i === 1 ? 0 : 1; t <= n; t++) {
      const p = { x: a.x + (b.x - a.x) * t / n, z: a.z + (b.z - a.z) * t / n };
      S.push({ p, s: s + L * t / n, c: kind(p) });
    }
    s += L;
  }
  const i0 = S.findIndex(x => x.c === 'L');
  if (i0 < 0) return null;
  let i1 = i0;   // the run's last lava sample; a dry sample or two between lava ones is still the run
  for (let i = i0 + 1; i < S.length; i++) {
    if (S[i].c === 'L') i1 = i;
    else if (S[i].s - S[i1].s > 0.6) break;
  }
  const onIt = i0 === 0;
  let from = S[0].p;
  if (!onIt) { let j = i0 - 1; while (j > 0 && S[i0].s - S[j].s < 0.5) j--; from = S[j].p; }
  let to = null;
  for (let i = i1 + 1; i < S.length; i++) if (S[i].s - S[i1].s >= margin && S[i].c === '.') { to = S[i].p; break; }
  if (!to && i1 + 1 < S.length && S[S.length - 1].c === '.') to = S[S.length - 1].p;   // the walk ends just past it
  const len = S[i1].s - S[i0].s + 0.25;
  // Iteration 33: the straight line from -> to at any length (lineOk), and the lava left on it past a 5.5 m dash
  // (lavaPast; dashNear when it is LAVA_PAST_OK or less - a dash, then a step or the second charge from on it). A stretch
  // of '#' up to LINE_SEAM m where the dry ground meets the lava ('.' on one side of it, 'L' on the other) is a seam of the
  // grid's sampling (a cell is '#' when the navmesh's nearest point is > 0.45 of a step off it - the game's path itself
  // walks through it), not a wall; '#' with lava on both sides is rock. Every "walking across 4.2-5.8 m of lava (no
  // straight line for a dash)" in runs 030-042 (5 of them, ~90 hp each with the burn) was a line over 5.5 m or a seam.
  let dashOk = false, lineOk = false, lavaPast = 0, dashLen = to ? dist(from, to) : 0;
  if (to && dashLen <= 9) {
    lineOk = true;
    const n = Math.max(2, Math.ceil(dashLen / 0.25)), ds = dashLen / n;
    let seam = 0, before = '.';
    for (let t = 1; t <= n && lineOk; t++) {
      const c = t === n ? '.' : kind({ x: from.x + (to.x - from.x) * t / n, z: from.z + (to.z - from.z) * t / n });
      if (c === 'L' || c === '.') {
        if (seam > 0 && (seam > LINE_SEAM || c === before)) lineOk = false;
        seam = 0; before = c;
        if (c === 'L' && t * ds > LAVA_DASH_M) lavaPast += ds;
      } else seam += ds;
    }
    dashOk = lineOk && dashLen <= LAVA_DASH_M;
  }
  const dashNear = lineOk && lavaPast <= LAVA_PAST_OK;
  return { from, to, start: S[i0].s, end: S[i1].s, len, onIt, dashLen, dashOk, lineOk, lavaPast, dashNear, total: s };
}
// Where to shoot a target from that stands across the lava (iteration 17): run-023's second Flame of Pyrana
// (Infernus's shield holds until every pillar dies; they spawn at random points, some on or beyond the lava -
// they are immune to it) stood ~15 m off with 2.9 m of lava on the way; the hero circled 14-18 m from it for
// 11 s and never hit it (the attack reaches 8.65 m; plan()'s cells are the 8 m round the hero, and its orbit
// keeps the distance it has when the lava stops it closing in). This: a dry cell with room (clear >= 2) within
// `reach` of the target and not within keep.r of keep.pos, the one the hero walks to soonest (grid reach), or -
// none reachable on foot - one a dash across the lava gets to from a reachable cell (the edge will do) 2.5-5.5 m
// short of it (the line over dry ground or lava only; cost the walk + 4). { p, walkTo, hop, cost } or null. Pure.
function shootSpot(g, target, reach, keep = null) {
  if (!g || typeof g.walk !== 'string' || !target) return null;
  const wet = wetCells(g);
  const ok = k => g.walk[k] === '.' && g.clear[k] >= 2 && !wet(k);
  const cand = [];
  for (let k = 0; k < g.walk.length; k++) {
    if (!ok(k)) continue;
    const p = gridPos(g, k);
    if (dist(p, target) > reach || (keep && dist(p, keep.pos) < keep.r)) continue;
    cand.push({ k, p });
  }
  let best = null;
  for (const c of cand) if (g.reach[c.k] >= 0) {
    const cost = g.reach[c.k] * g.step;
    if (!best || cost < best.cost) best = { p: c.p, walkTo: c.p, hop: false, cost };
  }
  if (best || !cand.length || typeof g.hazard !== 'string') return best;
  const from = [];
  for (let k = 0; k < g.walk.length; k++) {
    if (g.reach[k] < 0 || g.walk[k] !== '.' || wet(k)) continue;   // the edge itself will do to dash from
    const p = gridPos(g, k);
    if (dist(p, target) <= reach + 5.5) from.push({ k, p });
  }
  for (const c of cand) for (const f of from) {
    const md = dist(f.p, c.p);
    if (md < 2.5 || md > 5.5) continue;
    const cost = g.reach[f.k] * g.step + 4;
    if (best && cost >= best.cost) continue;
    const n = Math.max(2, Math.ceil(md / (g.step * 0.5)));
    let lineOk = true, lava = 0;
    for (let s = 1; s < n && lineOk; s++) {
      const q = gridCell(g, { x: f.p.x + (c.p.x - f.p.x) * s / n, z: f.p.z + (c.p.z - f.p.z) * s / n });
      if (q < 0) lineOk = false;
      else if (g.hazard[q] === 'L') lava++;
      else if (g.walk[q] !== '.') lineOk = false;
    }
    if (lineOk && lava > 0) best = { p: c.p, walkTo: f.p, hop: true, cost };
  }
  return best;
}
// Iteration 24 (run-036's death, Room_LavaLand_Combat_0_1): the clears-on-enter part lay beyond a lava field; the
// navmesh's path (the shortest line, lava being navmesh) crossed 24.8 m of it; no dry way round existed (the part is
// an island - runs 030-032 reached it by a 4.5-5.8 m crossing further west); lavaStep had only "the navmesh's own
// crossing, else walk it" and walked into it at 168/492: 168 -> 0 in 2.3 s, 11 m in. The lava ticks 0.03 of max hp
// every 0.25 s (x1.025 a tick) and lights the burn: ~0.15 of max hp a second in all (run-036's probe), ~4.5 m/s.
// lavaWalkCost: the expected damage of walking `len` m of it (+ ~0.06 of max for the burn's tail after stepping
// off). Pure.
const LAVA_DASH_M = 5.5, LAVA_WALK_HP = 0.3, LAVA_HOP_EXTRA = 45;
const LAVA_REFUSE_MS = 8000, LAVA_STALL_HP = 0.5;
// Iteration 33: a dash over the way's own crossing that lands up to LAVA_PAST_OK m short of its far side (then a step or
// the second charge from on it), and the '#' seam a dash line may cross (lavaRun).
const LAVA_PAST_OK = 1.5, LINE_SEAM = 1.0;
const lavaWalkCost = (len, maxHp) => maxHp * (0.15 * Math.max(0, len) / 4.5 + 0.06);
// The best short crossing between the hero's dry ground and the goal's (iteration 24): the grid's reach is the
// hero's side; a walk over dry cells ('.') from the goal's cells gives the goal's side; the pair (a on the hero's
// side, b on the goal's) 1-6.5 m apart whose straight line runs over lava or dry ground only (a dash stops at a wall)
// with the least walk + dash + walk. Beyond LAVA_DASH_M the dash lands on the lava up to 1 m short of its far edge (a
// tick at most; lavaStep dashes on from on it with the second charge) - 3 m more in the cost. Grid cells stand ~0.5 m
// off the lava's edge on each side, so a 4.5 m strip is a ~5.5 m hop. null: the goal outside the grid, on the hero's side already (a dry way), or no
// such pair. { from, to, dashLen, lava (m on the line), cost (m) }. Pure; tests/iter24.test.mjs.
function hopWay(g, goal, maxDash = LAVA_DASH_M + 1) {
  if (!g || typeof g.walk !== 'string' || typeof g.hazard !== 'string' || !goal || !Array.isArray(g.reach)) return null;
  const N = g.size * g.size, wet = wetCells(g);
  const dry = k => g.walk[k] === '.' && !wet(k);
  const gi = gridCell(g, goal);
  if (gi < 0) return null;
  const rg = new Int32Array(N).fill(-1), q = [];
  const near = Math.max(1.5, g.step * 1.5);
  for (let k = 0; k < N; k++) if (dry(k) && dist(gridPos(g, k), goal) <= near) { rg[k] = 0; q.push(k); }
  for (let h = 0; h < q.length; h++) {
    const k = q[h], i = k % g.size, j = Math.floor(k / g.size);
    for (let dj = -1; dj <= 1; dj++) for (let di = -1; di <= 1; di++) {
      const ni = i + di, nj = j + dj;
      if ((!di && !dj) || ni < 0 || nj < 0 || ni >= g.size || nj >= g.size) continue;
      const n = nj * g.size + ni;
      if (rg[n] >= 0 || !dry(n)) continue;
      rg[n] = rg[k] + 1; q.push(n);
    }
  }
  const lavaNear = k => {   // a lava cell within 2 cells
    const i = k % g.size, j = Math.floor(k / g.size);
    for (let dj = -2; dj <= 2; dj++) for (let di = -2; di <= 2; di++) {
      const ni = i + di, nj = j + dj;
      if (ni >= 0 && nj >= 0 && ni < g.size && nj < g.size && g.hazard[nj * g.size + ni] === 'L') return true;
    }
    return false;
  };
  const A = [], B = [];
  for (let k = 0; k < N; k++) {
    if (!dry(k)) continue;
    if (g.reach[k] >= 0 && rg[k] >= 0) return null;   // the two sides meet: a dry way (safePath's)
    if (g.reach[k] >= 0 && lavaNear(k)) A.push(k);
    else if (rg[k] >= 0 && lavaNear(k)) B.push(k);
  }
  let best = null;
  for (const a of A) {
    const pa = gridPos(g, a), ca = g.reach[a] * g.step;
    if (best && ca >= best.cost) continue;
    for (const b of B) {
      const pb = gridPos(g, b), d = dist(pa, pb);
      if (d < 1 || d > maxDash) continue;
      const cost = ca + d + rg[b] * g.step + (d > LAVA_DASH_M ? 3 : 0) + (g.clear[b] < 2 ? 1 : 0);
      if (best && cost >= best.cost) continue;
      const n = Math.max(2, Math.ceil(d / 0.25));
      let ok = true, lava = 0;
      for (let s = 1; s < n && ok; s++) {
        const c = gridCell(g, { x: pa.x + (pb.x - pa.x) * s / n, z: pa.z + (pb.z - pa.z) * s / n });
        if (c < 0) ok = false;
        else if (g.hazard[c] === 'L') lava += d / n;
        else if (g.walk[c] !== '.') ok = false;
      }
      if (ok && lava > 0) best = { from: pa, to: pb, dashLen: d, lava, cost };
    }
  }
  return best;
}
// Iteration 34: the room's campfires (Forest_Fireplace: position, radius), read once at room entry. One /reflect/find
// with no retries (a room without one answers an empty list).
async function readFireplaces() {
  let refs = [];
  try { refs = ((await call('GET', '/reflect/find?' + new URLSearchParams({ type: FIREPLACE_TYPE, limit: '5' }))).objects || []).map(o => o.$ref).filter(x => x != null); } catch { return []; }
  const out = [];
  for (const ref of refs) {
    const [pos, radius] = await Promise.all([peek(`$${ref}.position`), peek(`$${ref}.radius`)]);
    const c = vecOf(pos);
    if (c) out.push({ centre: c, radius: typeof radius === 'number' ? radius : null });
  }
  return out;
}
// Iteration 34: a walk to `target` that the game's own path would take through a fixed fire (a campfire): round it on the
// grid (fireAvoidPath), stepping out first when standing in one. true: go on (no fire on the way, walked round, or no way
// round found - the game's way then); false: the target lies in the fire itself, or enemies came.
async function fireDetour(target, what) {
  const fires = fireCircles(pools, Date.now());
  if (!fires.length) return true;
  // Iteration 41: the target inside the fire's hit reach is left; one beside it is walked to from the side away from the fire
  // (fireApproach), and the interact goes on from there.
  let ap = fireApproach(target, fires);
  if (ap && ap.inFire) { const core = ap.inFire; log(`  ${what}: ${dist(target, core.centre).toFixed(1)}m from the ${core.by || 'fire'} at (${core.centre.x.toFixed(1)}, ${core.centre.z.toFixed(1)}) - in it, left`); return false; }
  if (ap) {
    const h0 = await get('/hero').catch(() => null);
    const g = h0 && h0.position ? await get('/nav/grid', { radius: Math.min(40, Math.ceil(dist(h0.position, target) + 4)), step: 1 }).catch(() => null) : null;
    ap = fireApproach(target, fires, g) || ap;
    const f = ap.fire, dT = dist(target, f.centre);
    const path = h0 && h0.position && g ? fireAvoidPath(g, h0.position, ap.p, fires, 0.8) : null;
    const tag = `${dT.toFixed(1)}m from the ${f.by || 'fire'} (kept ${f.radius.toFixed(1)}m off, it hits within ${f.hit != null ? f.hit.toFixed(2) : '?'} m)`;
    if (path) {
      if (path.length) log(`  ${what}: ${tag} - up to it from the side away from the fire, (${ap.p.x.toFixed(1)}, ${ap.p.z.toFixed(1)}), in ${path.length} legs`);
      for (const w of path) {
        await tryPost('/hero/move', { x: w.x, z: w.z, wait: true, timeout: 2 + dist((await get('/hero')).position, w) / 3 });
        if (await enemyNear()) return false;
      }
      return true;
    }
    log(`  ${what}: ${tag} - no way on the grid to its side away from the fire, the usual way round`);
  }
  for (let tries = 0; tries < 3; tries++) {
    const h = await get('/hero').catch(() => null);
    if (!h || !h.position) return true;
    const me = h.position;
    const inF = fires.find(f => dist(me, f.centre) < f.radius);
    if (inF) {
      const o = fireStepOut(me, inF, target);
      log(`  ${what}: standing ${dist(me, inF.centre).toFixed(1)}m from the ${inF.by || 'fire'} - out of it first`);
      await tryPost('/hero/move', { x: o.x, z: o.z, wait: true, timeout: 3 });
      continue;
    }
    const np = await get('/nav/path', { x: target.x, z: target.z }).catch(() => null);
    if (!np || !Array.isArray(np.legs) || !np.legs.length) return true;
    const f = fireOnWay([me, ...np.legs.map(l => l.to)], fires);
    if (!f) return true;
    const need = Math.max(dist(me, target), ...np.legs.map(l => dist(me, l.to))) + 3;
    const step = need > 40 ? 1.5 : 1, radius = Math.min(Math.ceil(need), step * 40);
    const g = await get('/nav/grid', { radius, step }).catch(() => null);
    const path = fireAvoidPath(g, me, target, fires);
    if (!path) { log(`  ${what}: the way passes the ${f.by || 'fire'} at (${f.centre.x.toFixed(1)}, ${f.centre.z.toFixed(1)}) and no way round it on the grid - the game's way`); return true; }
    log(`  ${what}: the way passes the ${f.by || 'fire'} at (${f.centre.x.toFixed(1)}, ${f.centre.z.toFixed(1)}), r ${f.radius.toFixed(1)} - round it in ${path.length} legs`);
    for (const w of path) {
      await tryPost('/hero/move', { x: w.x, z: w.z, wait: true, timeout: 2 + dist((await get('/hero')).position, w) / 3 });
      if (await enemyNear()) return false;
    }
    return true;
  }
  return true;
}
// Walk to `target` off the lava (LavaLand only). true: there (or no hazard about), false: no safe way.
async function walkSafe(target, what) {
  if (!target) return true;
  // Iteration 34: round the room's campfires first (any zone).
  if (!(await fireDetour(target, what))) return false;
  if (!onLavaZone()) return true;
  const h = await get('/hero');
  const d = dist(h.position, target);
  // Iteration 17: the game's own path first, at any distance - /hero/move and /hero/interact walk it. run-023's
  // Combat_0_3: after the merchant the loot walked ~18 m south in one straight line back across the room's lava
  // strip (4 ticks, 81, and ~66 of the burn), a walk nothing logged; the grid check alone let it through. A
  // dry path is walked as it is; lava on it: round it on the grid or not at all.
  const np = await get('/nav/path', { x: target.x, z: target.z }).catch(() => null);
  if (np && !(np.onHazard > LAVA_PATH_OK)) return true;
  if (d < 4 && !np) return true;
  if (d > 58) { log(`  ${what} @${d.toFixed(0)}m left: too far to check for lava on the way`); return false; }
  const radius = Math.ceil(d + 3), step = radius > 40 ? 1.5 : 1;
  let g = null;
  try { g = await get('/nav/grid', { radius, step }); } catch { return !np; }
  if (!g.hazardCells && !np) return true;
  const path = g.hazardCells ? safePath(g, target, Math.max(3, step * 2)) : null;
  if (!path) { log(`  ${what} @${d.toFixed(0)}m left: no way to it off the lava${np ? ` (${np.onHazard.toFixed(1)}m of it on the way)` : ''}`); return false; }
  if (path.length > 1) log(`  ${what} @${d.toFixed(0)}m: round the lava in ${path.length} legs`);
  for (const w of path) {
    await tryPost('/hero/move', { x: w.x, z: w.z, wait: true, timeout: 2 + dist((await get('/hero')).position, w) / 3 });
    if (await enemyNear()) return false;
  }
  // The last steps (the grid's cell is within 3 m of it; the walk or the interact goes on from there).
  const np2 = await get('/nav/path', { x: target.x, z: target.z }).catch(() => null);
  if (np2 && np2.onHazard > LAVA_PATH_OK) { log(`  ${what}: the last steps to it cross ${np2.onHazard.toFixed(1)}m of lava - left`); return false; }
  return true;
}
// A walk that has to cross the lava (iteration 17): the fight's quiet walks (to the next part of the room, the
// clears-on-enter point, the exit) and the walk to the exit after the loot. run-023's Combat_0_3: the way in and
// the way out crossed its strip on foot (3 ticks + ~37 of burn; 2 ticks + ~13). One step of it, for a loop that
// calls it again: null - no lava on the way (the caller walks as usual); 'round' - a dry way not much longer,
// its first leg ordered; 'edge' - walking to the lava's edge; 'dash' - dashed across it; 'wait' - at the edge
// for a dash charge (3.6 s at most, then walked). st: the caller's state across calls ({}).
const LAVA_PATH_OK = 0.25, LAVA_DASH_WAIT = 3600;
async function lavaStep(goal, what, st) {
  if (!onLavaZone() || !goal) return null;
  const np = await get('/nav/path', { x: goal.x, z: goal.z }).catch(() => null);
  if (!np || !(np.onHazard > LAVA_PATH_OK) || !Array.isArray(np.legs) || !np.legs.length) { st.wait = 0; return null; }
  const hero = await get('/hero').catch(() => null);
  if (!hero || !hero.position) return null;
  const me = hero.position;
  const pts = [me, ...np.legs.map(l => l.to)];
  // The grid (up to 81 x 81 cells) is read once per goal every 1.5 s: its lava does not move (the rooms seen so
  // far: enableTranslation false) and the dry way's points are absolute.
  const key = Math.round(goal.x) + ':' + Math.round(goal.z);
  if (!st.g || st.key !== key || Date.now() - st.gAt > 1500) {
    // Iteration 24: a goal beyond 40 m gets a coarser grid (81 cells a side at most) that still holds it - run-036's
    // part was ~43 m off, and with it the goal's side of the lava (hopWay) and a dry way round.
    const need = Math.max(dist(me, goal), ...pts.map(p => dist(me, p))) + 3;
    const step = need > 40 ? 1.5 : 1, radius = Math.min(Math.ceil(need), step * 40);
    st.g = await get('/nav/grid', { radius, step }).catch(() => null);
    if (st.key !== key) { st.said = null; st.wait = 0; }
    st.gAt = Date.now(); st.key = key; st.hop = undefined;
    st.dry = st.g && st.g.hazardCells ? safePath(st.g, goal, 3) : null;
  }
  const g = st.g;
  if (!g) return null;
  const dry = st.dry;
  const run = lavaRun(g, pts);
  // The navmesh's own crossing, taken by a dash: <= 9 m with a straight line (or from on it).
  // Iteration 33: or a line a little longer than a dash (dashNear), seams allowed (lavaRun).
  const dashable = !!(run && run.to && run.len <= 9 && (run.dashOk || run.onIt || run.dashNear));
  if (dry && dry.length) {
    let i = 0;
    for (let j = 1; j < dry.length; j++) if (dist(me, dry[j]) < dist(me, dry[i])) i = j;
    if (dist(me, dry[i]) < 1.5 && i + 1 < dry.length) i++;
    let L = dist(me, dry[i]);
    for (let j = i + 1; j < dry.length; j++) L += dist(dry[j - 1], dry[j]);
    // Iteration 24: a dry way of any length when the navmesh's crossing cannot be dashed (it would be walked).
    if (L <= np.length + 15 || (!dashable && !(run && run.onIt))) {
      if (st.said !== 'round') { st.said = 'round'; log(`  ${what}: ${np.onHazard.toFixed(1)}m of lava on the way - round it (${L.toFixed(0)}m against ${np.length.toFixed(0)}m)`); }
      await tryPost('/hero/move', { x: dry[i].x, z: dry[i].z });
      return 'round';
    }
  }
  if (!run) return null;   // beyond the grid: walk on, look again
  const mv = (hero.skills || []).find(k => k.slot === 'Movement');
  const canDash = !!(mv && mv.type && mv.trigger && mv.trigger.canCast);
  // Iteration 24: the navmesh's crossing too long for a dash - a short crossing elsewhere (hopWay): the dry way to
  // its near edge, then a dash over. run-036: 24.8 m on the game's path, ~5 m further west.
  if (!dashable && !run.onIt) {
    if (st.hop === undefined) st.hop = hopWay(g, goal);
    const hop = st.hop;
    if (hop && hop.cost <= np.length + LAVA_HOP_EXTRA) {
      // A walk that stalls short of the edge (within 2 m, not moving for ~1 s) dashes from there.
      st.hopStill = st.hopAt && dist(st.hopAt, me) < 0.3 ? (st.hopStill || 0) + 1 : 0;
      st.hopAt = me;
      if (dist(me, hop.from) > 0.9 && !(st.hopStill >= 4 && dist(me, hop.from) <= 2)) {
        if (st.said !== 'hop') { st.said = 'hop'; log(`  ${what}: ${run.len.toFixed(1)}m of lava on the game's way - a dash across ${hop.lava.toFixed(1)}m of it from (${hop.from.x.toFixed(1)}, ${hop.from.z.toFixed(1)}) instead (${hop.cost.toFixed(0)}m against ${np.length.toFixed(0)}m)`); }
        const legs = safePath(g, hop.from, 0.8);
        let w = hop.from;
        if (legs && legs.length) {
          let i = 0;
          for (let j = 1; j < legs.length; j++) if (dist(me, legs[j]) < dist(me, legs[i])) i = j;
          if (dist(me, legs[i]) < 1.2 && i + 1 < legs.length) i++;
          w = dist(me, legs[i]) < 1.2 ? hop.from : legs[i];
        }
        await tryPost('/hero/move', { x: w.x, z: w.z });
        return 'hop';
      }
      if (canDash) {
        const k = Math.min(1, LAVA_DASH_M / Math.max(0.1, dist(me, hop.to))), at = { x: me.x + (hop.to.x - me.x) * k, z: me.z + (hop.to.z - me.z) * k };
        const r = await tryPost('/hero/cast', { slot: 'Movement', x: at.x, z: at.z, move: false });
        if (!r.error) {
          log(`  lava: a dash across ${hop.lava.toFixed(1)}m of it on the way to ${what} (${dist(me, at).toFixed(1)}m${k < 1 ? `, ${(dist(me, hop.to) - dist(me, at)).toFixed(1)}m short of the far side` : ''}; the game's way had ${run.len.toFixed(1)}m)`);
          st.wait = 0; st.said = 'dash'; st.g = null;   // read again from the far side
          await sleep(Math.round(dist(me, at) / 12 * 1000) + 80);
          return 'dash';
        }
      }
      // No charge: at the edge until one is back (~3.3 s a charge) - never walked.
      if (!st.wait) { st.wait = Date.now(); log(`  ${what}: at the lava's edge (${hop.lava.toFixed(1)}m of it) - waiting for a dash charge`); }
      await tryPost('/hero/move_dir', { x: 0, z: 0 });
      return 'wait';
    }
  }
  // Iteration 24: never on foot over a stretch longer than a dash covers, nor one whose expected damage is more than
  // LAVA_WALK_HP of the hp now (run-036: 24.8 m at 168/492 - ~440 expected). Decided before the walk to the edge.
  const walkLen = run.len;
  const walkCost = lavaWalkCost(walkLen, hero.maxHp);
  // Iteration 36: a refusal that goes on is worse than the burn - run-045 stood 67 s at full hp (492) before 7.4 m of
  // lava that was the only way on (~151 expected), no enemy came and the run ended "stuck". After LAVA_REFUSE_MS of
  // refusing, a walk costing up to LAVA_STALL_HP of the hp now is taken.
  const stalled = st.refusedAt && Date.now() - st.refusedAt >= LAVA_REFUSE_MS && walkCost <= LAVA_STALL_HP * hero.hp;
  const walkOk = run.onIt || stalled || (walkLen <= LAVA_DASH_M && walkCost <= LAVA_WALK_HP * hero.hp);
  const near = run.onIt || dist(me, run.from) <= 1.0;
  if (!near && (dashable || walkOk)) {
    if (st.said !== 'edge') { st.said = 'edge'; log(`  ${what}: ${run.len.toFixed(1)}m of lava ${run.start.toFixed(0)}m ahead - to its edge${dashable ? `, then a dash across${run.dashOk ? '' : ` (${run.dashLen.toFixed(1)}m line, ${run.lavaPast.toFixed(1)}m of lava past the dash)`}` : ` (the line over it ${run.to ? `${run.dashLen.toFixed(1)}m, ${run.lineOk ? `${run.lavaPast.toFixed(1)}m of lava past a dash` : 'blocked'}` : 'ends on it'})`}`); }
    await tryPost('/hero/move', { x: run.from.x, z: run.from.z });
    return 'edge';
  }
  if (dashable) {
    if (canDash) {
      const k = Math.min(1, 5.5 / Math.max(0.1, dist(me, run.to))), at = { x: me.x + (run.to.x - me.x) * k, z: me.z + (run.to.z - me.z) * k };
      const r = await tryPost('/hero/cast', { slot: 'Movement', x: at.x, z: at.z, move: false });
      if (!r.error) {
        log(`  lava: a dash across ${run.len.toFixed(1)}m of it on the way to ${what} (${dist(me, at).toFixed(1)}m)${run.onIt ? ' - from on it' : ''}`);
        st.wait = 0; st.said = 'dash';
        await sleep(Math.round(dist(me, at) / 12 * 1000) + 80);
        return 'dash';
      }
    } else if (!run.onIt) {
      if (!st.wait) { st.wait = Date.now(); log(`  ${what}: at the lava's edge (${run.len.toFixed(1)}m of it) - waiting for a dash charge`); }
      // Iteration 24: past the wait only a walk that is cheap enough; else wait on for the charge.
      if (Date.now() - st.wait < LAVA_DASH_WAIT || !walkOk) { await tryPost('/hero/move_dir', { x: 0, z: 0 }); return 'wait'; }
    }
  }
  if (!walkOk) {
    if (!st.refusedAt) st.refusedAt = Date.now();
    if (st.said !== 'refused') { st.said = 'refused'; log(`  ${what}: ${walkLen.toFixed(1)}m of lava on the way and no dash over it, no dry way - not walked (${walkLen > LAVA_DASH_M ? 'longer than a dash' : `~${Math.round(walkCost)} of it expected at ${Math.round(hero.hp)} hp`})`); }
    await tryPost('/hero/move_dir', { x: 0, z: 0 }); await tryPost('/hero/stop');
    return 'refused';
  }
  if (st.said !== 'walk') { st.said = 'walk'; log(`  ${what}: walking across ${run.len.toFixed(1)}m of lava (${!run.to ? 'the way ends on it' : run.len > 9 ? 'too long for a dash' : !dashable && !run.lineOk ? `no straight line for a dash (${run.dashLen.toFixed(1)}m)` : !dashable ? `${run.lavaPast.toFixed(1)}m of it past a dash` : 'no dash charge'}; ~${Math.round(walkCost)} expected at ${Math.round(hero.hp)} hp)`); }
  return null;
}
// The same, walked through (the exit after the loot): until the way has no lava left, 20 s at most.
async function crossLava(goal, what, maxMs = 20000) {
  const st = {}, t = Date.now();
  while (Date.now() - t < maxMs) {
    const r = await lavaStep(goal, what, st);
    if (!r || r === 'refused') return;
    if (isDead(await get('/state').catch(() => null))) throw new Dead();
    if (r !== 'dash') await sleep(300);
  }
}
// Off the hazard: the nearest standable cell (/nav/grid's `hero` is that cell when the hero stands on
// hazard ground).
async function leaveHazard(why) {
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  let g = null;
  try { g = await get('/nav/grid', { radius: 8, step: 1 }); } catch { }
  const h = g ? offHazardCell(g) : null;
  log(`  lava: ${why} - stopping${h ? ` and stepping off to (${h.x.toFixed(1)}, ${h.z.toFixed(1)})` : ''}`);
  if (h) await tryPost('/hero/move', { x: h.x, z: h.z, wait: true, timeout: 3 });
}

// --- lucid dreams (iteration 31) ---
// The run's modifiers: GameSettingsManager.activeLucidDreams (what GameManager.GetLucidDreams hands the run's start, one actor
// each), read through GET /lobby at the start of auto(). Decompiled from Dew.Contents.dll into history/it31/LucidDream_*.cs;
// the whole table (numbers, what each changes, proposals) is in lucid-dreams.md and NOTES iteration 31. `bot`: what the bot does
// differently while it is on. The 7 of the user's save first.
const LUCID = {
  LucidDream_SparklingDreamFlask: { what: 'Shrine_Guidance (Blessed too, a subclass) and regen orbs drop dream dust instead of healing', bot: 'Guidance shrines are dust within reach, used when the room is quiet; never walked to for health' },
  LucidDream_GrievousWounds: { what: 'heals and shields the heroes take x0.5', bot: 'nothing new: with the flask no shrine heals; treasures still bought when hurt (half the heal)' },
  LucidDream_EmbraceMortality: { what: 'every hit from one entity to another +100% (hero and monsters)', bot: 'nothing new (the loop was tuned under it)' },
  LucidDream_Overpopulation: { what: 'monster population (max and spawned) x1.5', bot: 'nothing new' },
  LucidDream_HarmlessWhispers: { what: 'bosses: no Unstoppable, +15% max hp, +10% AD/AP, 50+ tenacity; each 7th CC turns 14% of max hp into an armor shield', bot: 'Precision Shot not held on that armor shield (it does not decay; its break stuns)' },
  LucidDream_KindArmadillo: { what: 'every mirage skin (elite) becomes the plain armor one: the same shield, no special attacks, a stun when it breaks', bot: 'nothing new' },
  LucidDream_BonVoyage: { what: 'the hunter never advances on the map', bot: 'nothing new (the bot never raced the hunt)' },
  LucidDream_FalseLifeline: { what: 'each new room cleared: heroes healed 25% of missing hp (the knocked out revived at 25%); curses kept on a knockout', bot: 'nothing yet (proposal: wait for the heal before judging hp)' },
  LucidDream_FishScales: { what: "monster damage taken lowers max hp (30%, 40% from bosses, x0.65 melee) until the next zone; Guidance restores 25% (Blessed 50%); shields capped at max hp", bot: 'nothing yet (proposal: Guidance to win max hp back)' },
  LucidDream_BlandStarSoup: { what: "monsters' damage has no element (no burn, chill ... from them)", bot: 'nothing new' },
  LucidDream_MadLife: { what: 'enemy aim prediction 0.8-1.0 (they lead their shots)', bot: 'nothing yet (proposal: change direction more when shot at)' },
  LucidDream_PrudentJellyfish: { what: 'cooldown floors 40% (haste, upgrades) and cooldown reductions x0.6', bot: 'nothing new (the bot reads canCast)' },
  LucidDream_TheDarkestUrge: { what: 'monsters fight each other; a monster that kills one levels up (+135% of its base hp, +25% AD/AP, +10% speeds, +15 haste, a full heal)', bot: 'nothing yet (proposal: kill the levelled first)' },
  LucidDream_WILD: { what: 'every non-boss monster is a hunter (+50% move/attack speed, +50 haste, shadow walk, population x2) with the hunt level bonus', bot: 'nothing yet' },
  LucidDream_MarshOfDestiny: { what: 'a Seed of Torment shrine at each zone start: a reward (item, gold, dust, platinum) for a lasting penalty (monster stats, mirage chance, curses, a heroic boss skill)', bot: 'nothing new (an unknown shrine, left)' },
};
const GUIDANCE_DUST_DREAM = 'LucidDream_SparklingDreamFlask';
// One line for the run's start: each active one and what the bot does for it; a name the table does not know is said so.
function lucidLine(active, names = {}) {
  if (!active) return "lucid dreams: not read - each Guidance shrine judged by its own actionOverride";
  if (!active.length) return 'lucid dreams: none';
  return 'lucid dreams: ' + active.map(t => {
    const n = names[t] ? ` "${names[t]}"` : '';
    return LUCID[t] ? `${t.replace(/^LucidDream_/, '')}${n} (bot: ${LUCID[t].bot})` : `${t}${n} (unknown - no adaptation)`;
  }).join('; ');
}
// A Guidance shrine gives dust, not health: by the run's list when it was read (active: an array), else by the shrine's own
// actionOverride.Count (ovr; LucidDream_SparklingDreamFlask adds one to every Shrine_Guidance - the heal then never runs).
const guidanceGivesDust = (active, ovr) => Array.isArray(active) ? active.includes(GUIDANCE_DUST_DREAM) : (typeof ovr === 'number' && ovr > 0);
// How far one is worth the walk, weighed like a dust shard (FAR_DUST 16 m for ~25 dust, iteration 19): 16 m per 25 dust expected
// (GetSpecialRewardAmount_DreamDust x shrineAmount 1.2), between 16 and 24 m; 20 m when the amount was not read.
const GUIDANCE_REACH = { per: 16, shard: 25, min: 16, max: 24, unknown: 20 };
const guidanceDustReach = expected => typeof expected === 'number' && expected > 0 ?
  Math.max(GUIDANCE_REACH.min, Math.min(GUIDANCE_REACH.max, GUIDANCE_REACH.per * expected / GUIDANCE_REACH.shard)) : GUIDANCE_REACH.unknown;
// HarmlessWhispers: the shield a boss gets after its 7th crowd control (Se_HarmlessWhispers_GrantShield -> Se_MirageSkin_Armor, 14%
// of max) does not decay as Nyx's does - holding Precision Shot for it (bigShield) would wait on basic attacks; its break stuns.
const effectNames = effs => (Array.isArray(effs) ? effs : []).map(x => typeof x === 'string' ? x : x && (x.type || x.$type)).filter(Boolean);
const armorShield = (active, effs) => Array.isArray(active) && active.includes('LucidDream_HarmlessWhispers') && effectNames(effs).some(t => /MirageSkin_Armor/.test(t));
// --- end lucid dreams ---
let lucidActive = null;   // GET /lobby's activeLucidDreams (auto()), null when not read
const lucidNames = {};
async function readLucid() {
  try { const l = await get('/lobby'); lucidActive = l && Array.isArray(l.activeLucidDreams) ? l.activeLucidDreams.slice() : null; } catch { lucidActive = null; }
  // The in-game names, by the game's own localisation lookup (a pure getter); best effort.
  for (const t of lucidActive || []) {
    try { const n = await post('/reflect/call', { path: 'DewLocalization.GetUIValue', args: [t + '_Name'] }); if (typeof n === 'string' && n && n !== t + '_Name') lucidNames[t] = n; } catch { }
  }
  log(lucidLine(lucidActive, lucidNames));
  emit('lucid', { active: lucidActive, names: lucidNames });
}
// Per shrine (the fallback when the run's list was not read): its actionOverride.Count, read once.
const shrineOvr = new Map();
async function guidanceDust(i) {
  if (Array.isArray(lucidActive)) return guidanceGivesDust(lucidActive, null);
  if (!shrineOvr.has(i.id)) { const v = await peek(`#${i.id}.actionOverride.Count`); shrineOvr.set(i.id, typeof v === 'number' ? v : null); }
  return guidanceGivesDust(null, shrineOvr.get(i.id));
}
// The dust one gives in this zone (a pure getter, read once a zone).
let dustExpect = { zone: undefined, v: null };
async function guidanceDustExpected() {
  if (dustExpect.zone !== zoneSeen) {
    let v = null;
    try { v = await post('/reflect/call', { path: 'GameManager.instance.GetSpecialRewardAmount_DreamDust', args: [] }); } catch { }
    dustExpect = { zone: zoneSeen, v: typeof v === 'number' ? Math.round(v * 1.2) : null };
  }
  return dustExpect.v;
}
const dustLeftSaid = new Set();
const wellLullSaid = new Set();   // iteration 50: wells left in a lull (said once each)

// Iteration 31: a Guidance shrine under LucidDream_SparklingDreamFlask. The use (/hero/interact walks the hero to the pivot, well
// inside the r 15 range), explodeDelay (1 s) later the dust: PickupManager.DropDreamDust at the shrine's own position, a few
// pickups the hero collects by standing there (their ~4 s magnet otherwise; the loot's next pass walks to what is left).
// One line: the dust before -> after.
async function useDustShrine(it) {
  const d0 = (await get('/hero')).dreamDust;
  await tryPost('/hero/interact', { id: it.id });
  const t = Date.now();
  let usedAt = null, again = false;
  while (Date.now() - t < 5000) {
    if (await enemyNear()) return;
    const a = await peek(`#${it.id}.isAvailable`);
    if (a === false) { usedAt = Date.now(); break; }
    if (!again && Date.now() - t > 2500) { again = true; await tryPost('/hero/interact', { id: it.id }); }
    await sleep(150);
  }
  if (!usedAt) { log(`  dust: ${it.type} not used in 5 s - left`); return; }
  let d1 = d0, onIt = false;
  while (Date.now() - usedAt < 4000) {
    await sleep(250);
    const h = await get('/hero');
    d1 = h.dreamDust;
    // Onto the shrine's spot once the burst is due: the pickups land there.
    if (!onIt && Date.now() - usedAt > 1100 && it.position) {
      onIt = true;
      if (dist(h.position, it.position) > 1) await tryPost('/hero/move', { x: it.position.x, z: it.position.z, wait: true, timeout: 1.5 });
    }
    if (d1 > d0 && onIt) {
      const left = ((await get('/interactables', { radius: 10 })).pickups || []).filter(p => it.position && p.position && dist(p.position, it.position) < 4);
      if (!left.length) break;
    }
  }
  log(`  dust: ${it.type} ${d0} -> ${d1}${d1 > d0 ? ` (+${d1 - d0})` : ' (nothing yet - the pickups left to the loot)'}, ${((Date.now() - usedAt) / 1000).toFixed(1)}s after the use`);
}

// Shrines that heal, and the share of max health below which one is worth using. Decompiled:
// Shrine_Guidance heals healRatio of max health over a few ticks to whoever stands in its range
// when it goes off (it may cost gold - the cost check below applies); Shrine_BlessedGuidance sets
// health to max. Both are one use, and the heal lands a moment after the use, only in range.
// Before iter-3 the plain one was never used and the blessed one was used like any shrine - in
// run-002 in a lull of a fight, at 592/696: the heal was wasted, the next wave took 588 in 3 s, and
// the hero walked the rest of zone 1 at 10-35% and died two rooms later. Now they come last, only
// once the room is clear (never in a lull) and only when the hero is hurt.
// But no use has healed yet: run-004 3 plain + 1 blessed (543/1214 -> 543, the shrine used up),
// run-005 3 plain at 86/492 (57 m, 36 m, 6 m away: hp 86 all along, ~70 s of walks and waits). The
// code heals only entities inside the shrine's `range` collider when it goes off, explodeDelay after
// the use (Shrine_Guidance.OnUse) - and the use fires from up to 3 m off the shrine's interact
// collider (ActionInteract), so the hero may stand outside a small range. So now (useHealShrine): only
// within HEAL_SHRINE_DIST, walk onto the shrine first, use it, stand there and watch the hp; a type
// that gives nothing that way is not used again this run (healNothing) - one try of ~5-10 s at most.
// Iteration 12: Shrine_Guidance is dropped - 10 uses in the loop (run-004 3, run-005 3, run-009 1, run-013
// 1, run-016 1 standing on it at 64%: "316 -> 316"), never a point healed; each try cost 5-10 s. Why is
// still open (a manual test between runs would say). The blessed one stays (set health to max; 1 use seen).
// Iteration 23: both back (the user's call: the hero was never inside the heal's range). The heal goes to whoever
// stands inside the shrine's `range` DewCollider explodeDelay (1 s) after the use - the collider's own centre and
// scale (DewCollider.PositionColliders: transform position + offset, radius x lossyScale), not the interactable's
// position the bot walked to, and /hero/interact then walked it to the interact pivot. Now useHealShrine reads the
// range, stands at its centre, uses the shrine, steps back in if the use took it out, and holds still until the
// heal shows. Plain below 70%, blessed below 90%; next to the zone boss below HEAL_BEFORE_BOSS (bossHealPct).
const HEAL_SHRINES = { Shrine_Guidance: 0.7, Shrine_BlessedGuidance: 0.9 };
const HEAL_BEFORE_BOSS = 0.97;
// Decompiled (history/decomp-contents/Se_Curse_DreamAfflictionHallucinatory.cs): a curse that cuts every heal the
// hero takes (takenHealProcessor ApplyReduction) - the plain shrine's Se_GenericHealOverTime is a heal, so it is
// left; the blessed one sets health to max (Status.SetHealth), which the reduction does not touch.
const HEAL_CUT = /^Se_Curse_DreamAfflictionHallucinatory$/;
const healCut = hero => ((hero && hero.statusEffects) || []).find(e => HEAL_CUT.test(e.type || ''));
const HEAL_SHRINE_DIST = 30;
// Badly hurt, any distance a walk can be checked for (iter-11): run-014 left a Shrine of Guidance at 35 m
// with the hero at 271/900 ("too far"), went into Nyx at 30% and died with her at 21%. Caveat: no
// Guidance use has healed yet in this loop (run-004 3, run-005 3, run-009 1, run-013 1: +0 each; the
// last two stood on it) - so this is also the test of whether it ever does; one that gives nothing is
// not used again this run (healNothing), so it costs one walk a run at most.
const HEAL_LOW = 0.5, HEAL_FAR_DIST = 58;
// Iteration 24: run-036 used a Shrine_Guidance from 0.6 m of its range centre (r 15), stood still 4 s: 163 -> 163 - the
// 16th use in the loop to heal nothing, and 26 s of a room (a 42.9 m walk there). The decompiled use path is the one a
// player's F takes (DevTools /hero/interact = EntityControl.CmdInteract, as ControlManager does) and the shrine did
// count the use (isAvailable false, which only a successful OnUse leads to); what happens after (the burst's
// range.GetEntities, owner.isHumanPlayer, the heal) cannot be told from the logs - see NOTES iteration 24 and
// proposals/iter-24-mod.md. Until a heal shrine has healed once (healWorked), it is tried only HEAL_TRY_DIST or
// closer (a few seconds), with healProbe's reads logged at the use and after the burst to tell which step fails.
const HEAL_TRY_DIST = 15;
let healWorked = false;
const healReach = hpPct => Math.min(healWorked ? Infinity : HEAL_TRY_DIST, hpPct < HEAL_LOW ? HEAL_FAR_DIST : HEAL_SHRINE_DIST);
// Iteration 21: the Blessed Shrine of Guidance from the start - used twice in the loop, healed 0 both times (run-004 at
// 543/1214, 9 s; run-031 at 237/492 after a 42.8 m walk, 13 s: "delay 1s, heal 1 of max, range r 15", the hero 0.5 m off).
// Iteration 23: emptied - those tries stood at the interactable, not in the range (see HEAL_SHRINES).
const healNothing = new Set();

// Shrines never used. Destiny and Stardust as before. Decompiled (history/Shrine_PileOfSnow.cs,
// Shrine_Hatred.cs):
// - Pile of Snow: 5% of current health on each dig, then 45% nothing, 30% a little gold or dust,
//   15% a cold memory or essence, 10% Scavengers (4 of them, or a Scavenger miniboss, at times) -
//   next to the hero. run-003 zone 1 dug 5 of them: ~35 s of walks (12-21 m away) and Scavenger
//   fights (85 hp in one), 151 hp in digs, and the fifth in Skoll's room before the fight.
// - Hatred Remnant: a window of curse strengths (a curse on the hero, a reward when its quest is
//   done). The bot never answers it: it walks up, waits the use out (11-15 s) and leaves it there
//   to do again on the next pass - twice in one room in run-002, 23 times in run-003's loop.
// - Shrine of Dismantling (Shrine_Disintegration, history/it6): gold for health - 25% of max health
//   (+15% per earlier use) through Se_HealthCost. It stays available, so useShrine waited out its
//   whole limit each time: 14.7 s a use (run-001, 002, 005), and 55 / 106 hp (run-005, run-002).
const SKIP_SHRINES = new Set(['Shrine_Destiny', 'Shrine_Stardust', 'Shrine_PileOfSnow', 'Shrine_Hatred', 'Shrine_Disintegration']);
// Since iter-10 only shrines known to give something are used (run-001..011: an item or a choice of
// one, a stat, an upgrade, gold) - run-011 walked 63 m across lava to Pyrana's Love, a type never met
// before. Maw of Doom (12 s a use, nothing seen, run-006/007) and the others seen once or twice are
// left too. The boss soul and the wells are handled on their own; the healing ones by HEAL_SHRINES.
const GOOD_SHRINES = new Set(['Shrine_Memory', 'Shrine_Concept', 'Shrine_Retrospection', 'Shrine_Enlightenment', 'Shrine_Luck',
  'Shrine_FallenStar', 'Shrine_Chaos', 'Shrine_CorruptedChaos', 'Shrine_UpgradeWell', 'Shrine_BossSoul', 'Shrine_PotOfGreed']);
// And nothing but the boss soul beyond FAR_SHRINE (a 40 m walk is ~8-9 s each way).
const FAR_SHRINE = 40;
const unknownSaid = new Set();

// --- merchants and the clear (iteration 38) ---
// The user: "the bot keeps pestering the merchants". Runs 030-047: 17 merchant opens, 11 bought (all Jonas, all with the gold
// for his cheapest item), 6 bought nothing - 4 Souvenir Merchants (Smoothie) with no heal in stock, run-047's Jonas in zone 3
// with 164 gold (his cheapest 251; the trip there chosen by "gold >= 150"), and one Smoothie that "did not open" (6 s, in
// the lull of a Despair room before its clear; opened 9 s later, after the clear, in 1 s). What each merchant sells is known
// once opened (/interactables' stock) - remembered per room (merchantMemo), not re-read by opening it again.
// Jonas's prices (decompiled Gem/SkillTrigger.GetBuyGold: the rarity's value x the quality/level the zone rolls): the
// cheapest item seen in a stock or bought, per zone index, runs 001-047 - z0 110 (a Common essence; memories 115), z1 146
// (Snow) / 158 (DarkCave, LavaLand), z2 202 (Sky) / 230 (Ink), z3 251 (Despair). The Smoothie's treasures (Treasure.
// OnAddMerchandise, base 100): the cheapest seen z1 144, z2 136, z3 178. Its souvenirs (Acc_*) cost 200 stardust (Cost.
// Stardust(200), the gold price reads 0): cosmetic, never bought - not free items.
const SHOP_FLOOR = [110, 146, 202, 251], TREASURE_FLOOR = [100, 136, 136, 178];
const floorAt = (tbl, z) => typeof z !== 'number' || z < 0 ? tbl[0] : z < tbl.length ? tbl[z] : Math.round(tbl[tbl.length - 1] * Math.pow(1.25, z - tbl.length + 1));
// A trip to a merchant node: with the gold for the zone's cheapest item; one that costs a room for the fights (shopDetour) only
// with the gold for two of them (run-039 659 and run-047 668 gold in zone 2 bought 1 and 2 things; run-041 243 and run-043 365
// bought one Common/Rare essence each for the room the route then went over); never one that costs two.
const SHOP_ROOM_GOLD = 2;
function shopTrip(gold, zone, extra = 0) {
  const floor = floorAt(SHOP_FLOOR, zone);
  if (!(gold >= floor)) return { go: false, why: `${gold} gold, the cheapest there in zone ${zone} ~${floor}` };
  if (extra >= 2 || (extra >= 1 && gold < SHOP_ROOM_GOLD * floor)) return { go: false, why: `it costs ${extra} room${extra > 1 ? 's' : ''} for the fights, ${gold} gold (${extra >= 2 ? 'never worth two' : `under ${SHOP_ROOM_GOLD} x ~${floor}`})` };
  return { go: true, why: `shopping with ${gold} gold (the cheapest there ~${floor}; ${extra} room${extra === 1 ? '' : 's'} over the fights' way)` };
}
// A merchant in the room: open it (again)? memo: what an earlier open showed (stockMemo) and its failures. Not in a lull of the
// fight (the room not clear: run-047's Smoothie that did not open), not again after a failure, not again with the stock seen
// unless the gold (or the hurt, for a heal) now buys something it showed; unseen: Jonas with the gold for the zone's cheapest,
// the Smoothie when hurt enough for a heal and with the gold for one, any other with 60.
function merchantCall(i, memo, hero, zone, lull, heal) {
  const hp = hero.hp / hero.maxHp, pct = `${Math.round(100 * hp)}% hp`;
  if (lull) return { go: false, why: 'the room is not clear yet' };
  if (memo && memo.fails) return { go: false, why: `it did not open (${memo.fails}x) - left this room` };
  if (memo && memo.seen) {
    const h = memo.heals.find(x => hp < x.below && hero.gold >= x.price);
    if (h) return { go: true, why: `${h.item} (${h.price}) at ${pct}` };
    if (hero.gold >= memo.wantGold) return { go: true, why: `${hero.gold} gold buys what it showed (from ${memo.wantGold})` };
    return { go: false, why: `seen: nothing wanted ${memo.wantGold === Infinity ? 'in its stock' : `under ${memo.wantGold} gold`} (${hero.gold}, ${pct})` };
  }
  if (/Smoothie/.test(i.type || '')) {
    const below = Math.max(...Object.values(heal));
    if (!(hp < below)) return { go: false, why: `${pct}: it sells heals (below ${Math.round(100 * below)}%) and trinkets` };
    const f = floorAt(TREASURE_FLOOR, zone);
    if (hero.gold < f) return { go: false, why: `${hero.gold} gold, its treasures ~${f}` };
    return { go: true, why: `${pct}, ${hero.gold} gold - a heal if it has one` };
  }
  if (/Jonas/.test(i.type || '')) {
    const f = floorAt(SHOP_FLOOR, zone);
    return hero.gold >= f ? { go: true, why: `${hero.gold} gold` } : { go: false, why: `${hero.gold} gold, the cheapest there in zone ${zone} ~${f}` };
  }
  return hero.gold >= 60 ? { go: true, why: `${hero.gold} gold` } : { go: false, why: `${hero.gold} gold` };
}
// What an open showed: the least gold that buys something the shop wants (wanted: shop()'s list over the whole stock, the
// gold aside), and the heal treasures with the hurt each is bought at.
function stockMemo(stock, wanted, heal) {
  const heals = (stock || []).filter(x => x.count > 0 && x.type === 'Treasure' && heal[x.item] && x.price && !x.price.stardust)
    .map(x => ({ item: x.item, price: x.price.gold || 0, below: heal[x.item] }));
  const gold = (wanted || []).filter(w => !heal[w.x.item] && w.x.price && !(w.x.price.dreamDust > 0)).map(w => w.x.price.gold || 0);
  return { seen: true, fails: 0, wantGold: gold.length ? Math.min(...gold) : Infinity, heals };
}
// What a heal treasure gives now (decompiled: Sparkling Elixir 40% of max health at once, the excess a shield; Token of
// Guidance 40% of the missing health after each of the next 4 travels) - halved under LucidDream_GrievousWounds (iter-31).
function healLine(item, hero, lucid) {
  const cut = Array.isArray(lucid) && lucid.includes('LucidDream_GrievousWounds') ? 0.5 : 1;
  const miss = Math.max(0, hero.maxHp - hero.hp);
  const n = item === 'Treasure_SparklingElixir' ? 0.4 * hero.maxHp * cut : item === 'Treasure_TokenOfGuidance' ? 0.4 * miss * cut : 0;
  return `heals ~${Math.round(n)}${item === 'Treasure_TokenOfGuidance' ? ' after the next travel (then 3 more)' : ''}${cut < 1 ? ' (Grievous Wounds: half)' : ''}`;
}
const merchantMemo = new Map(), merchantSaid = new Set();   // merchant id -> stockMemo (+ fails); ids whose skip was logged; per room
// The stock for the log: souvenirs by their stardust, treasures that do not heal marked as such.
const stockLine = (stock, heal) => (stock || []).filter(x => x.count > 0).map(x => x.price && x.price.stardust ? `${x.item}/${x.price.stardust} stardust (souvenir)` :
  `${x.item}/${x.price && x.price.gold}${x.type === 'Treasure' && !heal[x.item] ? ' (not a heal)' : ''}`).join(' ');
// Iteration 38 (the user: "it runs to the portal while the shrines are still unlocking, back to the shrine, then to the portal
// again"). Decompiled: the reward shrines (RoomRewards.SpawnLockedShrineOnFinalSection - Memory, Concept, Retrospection,
// Enlightenment, Luck; room modifiers' shrines with lockedUntilCleared) are locked until Room.onRoomClear, which comes with
// the exit (ClearRoom -> OpenRifts) - but not with the last kill: RoomMonsters waits Dew.WaitForAggroedEnemiesRoutine (1.5 s
// when an enemy was within 8 m of the hero, then until the aggroed ones are dead). Meanwhile fight()'s quiet branch, with no
// enemy, no inactive combat area and no clears-on-enter part left, walked to the exit (run-045 Combat_3_4: 7 m out, then 8 m
// back to the Memory shrine). Now its goal is the nearest reward shrine not used yet (locked or not), up to UNLOCK_CAP of
// quiet; the hero waits beside it, and the loot after the clear starts there (nearest first).
const UNLOCK_CAP = 8000, UNLOCK_NEAR = 2.5;
// Not waited at: the boss soul (a boss room's own flow), a well (the loot walks to one only with the dust for an upgrade).
const UNLOCK_NOT = new Set(['Shrine_BossSoul', 'Shrine_UpgradeWell']);
function clearGoal(list, tried, good, maxD) {
  const c = (list || []).filter(i => i.kind === 'shrine' && i.position && good.has(i.type) && !UNLOCK_NOT.has(i.type) && !tried.has(i.id) &&
    i.details && i.details.shrine && i.details.shrine.available !== false && !(i.distance > maxD)).sort((a, b) => (a.distance ?? 99) - (b.distance ?? 99));
  return c.length ? { target: c[0], locked: c.filter(i => i.details.shrine.locked) } : null;
}
// One look of the quiet branch with nothing left to fight (the far enemies, inactive combat areas and clears-on-enter parts
// taken first by the caller): 'walk' to the nearest reward shrine, 'wait' beside it, or null - the exit, as before (a combat
// room only, the room not clear, within UNLOCK_CAP of the quiet).
function unlockStep(room, quietMs, cg, me) {
  if (!room || room.nodeType !== 'Combat' || room.exitOpen || room.enemiesAlive !== 0 || !(quietMs < UNLOCK_CAP) || !cg || !me) return null;
  const p = cg.target.position;
  return Math.hypot(me.x - p.x, me.z - p.z) <= UNLOCK_NEAR + 0.5 ? { act: 'wait', p } : { act: 'walk', p };
}
// --- end merchants and the clear ---

// Time the loot spent on nothing (run-007..009, 3 runs, ~700 s of loot in all):
//   - merchants with nothing to buy, visited again: the lull loot and the loot after the fight each
//     have their own `seen`, so a merchant was walked to and opened twice or three times a room
//     (run-009 z1 the souvenir merchant 3 times in 60 s, z2 Jonas twice; run-004 Jonas 4 times, Smoothie
//     3). Now one with nothing for us (checkedHere: the gold then) is left until the gold rises; the
//     souvenir merchant (Smoothie: treasures and hats - shop() buys only its healing treasures) only
//     when the hero is hurt enough for one (HEAL_TREASURES).
//   - Shimmering Wells with no upgrade affordable: run-009 z2 walked 35 m to one with 41 dust (6 s),
//     run-007 51 m (9 s) and 23 m, run-006 24 m - no upgrade. Now a well is left while the dream dust
//     is below the cheapest upgrade of a worn skill (the same pure getter upgradeAt() asks).
//   - deposits beyond FAR_DEPOSIT: 8.5 s each (38.5 m, 34.4 m) against 2.5 s under 10 m.
//   - the order: deposits first, then merchants, then shrines and items, each list in its own order -
//     now the nearest first, whatever it is (each pass reads the distances anew).
const FAR_DEPOSIT = 30, FAR_WELL = 20, ITEM_WAITS = 4;
// Gold shards with the gold for a purchase already in hand (iter-11): only within FAR_GOLD. Gold buys
// only at merchants, and since iter-8 a merchant is visited only when on the way - run-010 bought 2
// essences (169, 202 gold), run-011 and run-013 none; run-013 broke 20 gold shards for 57 s of its loot
// (30 s of it on the 10 beyond 8 m), run-009 18 for 58 s. The dearest buy seen: 307 (an Epic essence).
// Iteration 18: gold shards only within FAR_GOLD whatever the gold (GOLD_ENOUGH 0; was 350). Runs 018-026 broke
// 5-9 gold shards a run (~3.3 s each, 12 m on average: ~20 s a run) and bought 0-2 Common essences with it (146-158
// each); run-026's peaceful room (RoomMod_GiftMerchant, no enemy) spent 40 s, 5 gold shards of it. Within ~9 m a
// shard is broken from where the hero stands (the attack reaches 8.65 m) - those are still taken.
// Iteration 19: dream dust shards within FAR_DUST. run-027 walked 24.5 m (Forest node 9, ~6 s) and 28.8 m (Snow node
// 13, 7 s, the last thing before the boss room) for one shard each; a shard is ~25 dust (4 shards and a fight's drops
// -> 110 at the first well), a quarter to a half of one +1 on a skill. Within 16 m a shard is on the way or a short
// step off it.
const FAR_GOLD = 9, GOLD_ENOUGH = 0, FAR_DUST = 16;
const depositReach = (p, gold) => /Stone_Gold/.test(p.type || '') && gold >= GOLD_ENOUGH ? FAR_GOLD : /Stone_DreamDust/.test(p.type || '') ? FAR_DUST : FAR_DEPOSIT;
const checkedHere = new Map();   // id -> gold (merchant) or dust (well) when it had nothing for us; per room
// Iteration 37: the cheapest upgrade among the memories and the essences worth one (a well is walked to only with the dust).
async function cheapestUpgrade(hero) {
  const { memCost, gemCost } = await upgradeCosts(hero);
  const all = [...memCost.values(), ...gemCost.values()];
  return all.length ? Math.min(...all) : null;
}
// Iteration 37: an upgrade's cost by the game's own getter on the item (#id), else on its level / quality.
async function dustCost(method, id, fallback) {
  for (const arg of [id ? '#' + id : null, fallback]) {
    if (arg == null) continue;
    try { const c = await post('/reflect/call', { path: 'GameManager.instance.' + method, args: [arg] }); if (typeof c === 'number') return c; } catch { }
  }
  return null;
}
// The dust each worn memory's +1 level costs (slot -> dust) and each socketed essence's +quality (id -> dust); an essence
// that gains nothing (Dead, or of a form not valued) is not asked about.
async function upgradeCosts(hero) {
  const memCost = new Map(), gemCost = new Map();
  for (const k of ((hero && hero.skills) || []).filter(k => ['Q', 'W', 'E', 'R'].includes(k.slot) && k.type)) {
    const c = await dustCost('GetSkillUpgradeDreamDustCost', k.id, k.level);
    if (c != null) memCost.set(k.slot, c);
    for (const g of (k.gems || []).filter(g => g && g.type && g.id)) {
      const e = GEM_EFF[gemKey(g.type)];
      if (!e || e[0] === 'O' || !(e[4] > 0) || fitVerdict(g) === 'Dead') continue;
      const cg = await dustCost('GetGemUpgradeDreamDustCost', g.id, g.quality);
      if (cg != null) gemCost.set(g.id, cg);
    }
  }
  return { memCost, gemCost };
}
// The game's numbers the choice uses, read once (pure getters): skill haste a level, the cooldown floor by upgrades, the
// quality a well adds to an essence.
const upGes = { read: false };
async function upgradeGes() {
  if (upGes.read) return upGes;
  upGes.read = true;
  const h = await peek('GameManager.instance.ges.gainedSkillHastePerSkillLevel'), f = await peek('GameManager.instance.ges.cooldownFloorRatioBySkillUpgrade');
  if (typeof h === 'number') upGes.haste = h;
  if (typeof f === 'number') upGes.floor = f;
  try { const q = await post('/reflect/call', { path: 'GameManager.instance.GetGemUpgradeAddedQuality', args: [] }); if (typeof q === 'number') upGes.dq = q; } catch { }
  log(`  upgrades: skill haste ${upGes.haste ?? '?'} a level, cooldown floor ${upGes.floor ?? '?'}, a well adds ${upGes.dq ?? '?'} quality to an essence (unread: ${IMP.HASTE_LVL}, ${IMP.CD_FLOOR}, ${IMP.GEM_DQ})`);
  return upGes;
}
// Iteration 53: the dust budget's context (the zone's index for the bar, the reserve and the W/E keep; the final zone spends
// all), and what the policy would buy now with the hero's dust: { j (the first click's judgement), plan (dustPlan), dust }.
const dustCtx = () => ({ zone: lastRoom && typeof lastRoom.zoneIndex === 'number' ? lastRoom.zoneIndex : (zoneSeen ?? 0), final: isFinalZone(lastRoom) });
async function dustNow(hero) {
  const ges = await upgradeGes(), ctx = dustCtx();
  const { memCost, gemCost } = await upgradeCosts(hero);
  const cands = upgradeCands(hero, await impactRows(hero), { memCost, gemCost, ges, dq: ges.dq ?? IMP.GEM_DQ, dl: 1 });
  return { j: dustJudge(cands, hero.dreamDust, hero, ctx), plan: dustPlan(cands, hero.dreamDust, hero, { ...ctx, dq: ges.dq ?? IMP.GEM_DQ }), dust: hero.dreamDust };
}
// Iteration 37: the worn memories measured (memStats) with what the run has seen: /hero/use, the channel of each cast, the
// bot's casts in fights, the game's level-haste numbers.
async function impactRows(hero) {
  return memStats(hero, await heroUse(), { chan: chanOf(), tally: castTally, ges: await upgradeGes(), lvlAvg: lvlAvgOf(castTally) });
}
// /hero/use's memories (DevTools' MemoryUse), or null (an older build).
async function heroUse() {
  if (skillUse.off) return null;
  try { const r = await get('/hero/use'); return (r && r.memories) || null; } catch { return null; }
}

// lull: looting in a pause of a fight (enemies may still come) - no healing shrines then.
async function loot(radius = 70, maxPasses = 30, lull = false) {
  const seen = new Set();
  let waits = 0, saidFar = false, cheap = null;   // cheap: what the dust budget buys (dustNow), for the dust and loadout it was read at
  for (let pass = 0; pass < maxPasses; pass++) {
    if (await enemyNear()) { log(abortLine()); return; }
    await sortHands();
    // Iteration 25: a socketed essence that cannot fire where it is, moved where it can (when the loadout changed).
    if (!lull) await rehomeEssences();
    const { interactables, pickups } = await get('/interactables', { radius });
    const hero = await get('/hero');
    if (burning(hero)) await cleanseSelf(hero);
    // Nearest first (iteration 27: no back and forth across a boss room).
    for (const p of pickups.filter(p => p.distance > 1.5 && p.distance < Math.min(40, radius)).sort((a, b) => a.distance - b.distance).slice(0, 4)) {
      await waitOutFire(p.position, 'a pickup');
      if (!(await walkSafe(p.position, 'a pickup'))) { if (abortWhy) { log(abortLine()); return; } continue; }
      await tryPost('/hero/move', { x: p.position.x, z: p.position.z, wait: true, timeout: 6 });
    }
    const items = groundItems(interactables);
    const allProps = (await get('/entities', { kind: 'props', radius })).entities
      .filter(p => p.alive && /Stone_(Gold|DreamDust|Nightmare)/.test(p.type) && !seen.has(p.id));
    const props = allProps.filter(p => !(p.distance > depositReach(p, hero.gold))).map(p => ({ ...p, kind: 'prop', canInteract: true }));
    if (!saidFar && props.length < allProps.length) { saidFar = true; log(`  deposits left: ${allProps.filter(p => p.distance > depositReach(p, hero.gold)).map(p => `${p.type.replace(/^PropEnt_Stone_/, '')} @${p.distance}m`).join(', ')} (beyond ${FAR_DEPOSIT} m; dust beyond ${FAR_DUST} m, gold beyond ${FAR_GOLD} m)`); }
    // Iteration 38 (merchantCall): not in a lull of the fight, not again after it failed to open or with its stock seen and
    // nothing new within the gold (or the hurt, for a heal); one not opened yet by the zone's prices. Why one is left: once a room.
    const merchants = interactables.filter(i => {
      if (i.kind !== 'merchant' || seen.has(i.id)) return false;
      const c = merchantCall(i, merchantMemo.get(i.id), hero, lastRoom && lastRoom.zoneIndex, lull, healTreasures(lastRoom && lastRoom.zoneIndex));
      if (!c.go && !lull && !merchantSaid.has(i.id)) { merchantSaid.add(i.id); log(`  ${i.name || i.type} @${i.distance}m left: ${c.why}`); }
      return c.go;
    });
    // A well only with a buy the dust budget makes there (iteration 53: dustNow - the clicks that clear its bar; until then
    // the dust for the cheapest upgrade). Re-judged when the dust or the loadout changes (a memory or essence picked up).
    let wellOk = () => true;
    if (!lull && interactables.some(i => i.type === 'Shrine_UpgradeWell' && !seen.has(i.id) && i.canInteract)) {   // in a lull no well (iteration 50) - nor its reads
      const sig = `${hero.dreamDust}|${loadoutSig(loadoutOf(hero))}`;
      if (!cheap || cheap.sig !== sig) cheap = { sig, ...(await dustNow(hero)) };
      const buys = cheap.plan.buys.length;
      // Iteration 16: a far well (beyond FAR_WELL) only with two upgrades to make - run-022 Ink node 11 walked 25 m to one
      // for a single +1 (Ignite +2 for 60 with 61), run-020 z0 37 m (~8 s there and back). The dust is not lost: the next
      // well spends it.
      const need = i => i.distance > FAR_WELL ? 2 : 1;
      wellOk = i => {
        if (i.type !== 'Shrine_UpgradeWell' || buys >= need(i)) return true;
        if (checkedHere.get(i.id) !== sig) { checkedHere.set(i.id, sig); log(`  well @${i.distance}m left: ${buys ? `one buy (${cheap.plan.buys[0].name}) - beyond ${FAR_WELL} m two wanted` : dustLine(cheap.j, cheap.dust, hero)}`); }
        return false;
      };
    }
    const usable = i => i.details && i.details.shrine && i.details.shrine.available && !i.details.shrine.locked &&
      (!i.details.shrine.cost || (i.details.shrine.cost.gold || 0) <= hero.gold) && (!(i.details.shrine.cost && i.details.shrine.cost.healthPercentage) || hero.hp / hero.maxHp > 0.7);
    const known = i => {
      if (GOOD_SHRINES.has(i.type)) return true;
      if (!unknownSaid.has(i.type) && !SKIP_SHRINES.has(i.type)) { unknownSaid.add(i.type); log(`  shrine ${i.type} (${i.name || '?'}) @${i.distance}m left: not a kind known to give anything`); }
      return false;
    };
    // Left on purpose (too far): not a reason for the map's 'unclaimed rewards' walk back either (travel).
    const near = i => { const ok = i.type === 'Shrine_BossSoul' || items.includes(i) || !(i.distance > FAR_SHRINE); if (!ok) triedHere.add(i.id); return ok; };
    // Iteration 50 (run-054, Despair Combat_3_1): no well in a lull - its menu held the hero 3 s (three upgrades) with the room's
    // fight not over, and a Paralytic Bug (4504 hp) came on at once: 245 + 175 + 121 + 110, 1028 -> 397, carried to Azurak and
    // Primus. The well stays; the loot after the clear uses it (it did, 6 s later).
    const notLull = i => {
      if (!lull || i.type !== 'Shrine_UpgradeWell') return true;
      if (!wellLullSaid.has(i.id)) { wellLullSaid.add(i.id); log(`  well @${i.distance}m left for now: the room's fight is not over - used after the clear`); }
      return false;
    };
    const todo = props.concat(merchants.filter(near), interactables.filter(i => !seen.has(i.id) && i.canInteract && near(i) && (
      items.includes(i) ||
      (i.kind === 'shrine' && !HEAL_SHRINES[i.type] && !SKIP_SHRINES.has(i.type) && known(i) && usable(i) && wellOk(i) && notLull(i)))));
    // Iteration 31: a Guidance shrine that gives dust (guidanceDust: LucidDream_SparklingDreamFlask) is loot like a dust shard -
    // within guidanceDustReach, with the room quiet (never in a lull). It is flagged (i.dust) so the heal logic below leaves it.
    if (!lull) {
      const guid = interactables.filter(i => !seen.has(i.id) && i.kind === 'shrine' && HEAL_SHRINES[i.type] && i.canInteract && usable(i));
      let reach = null;
      for (const i of guid) {
        if (!(await guidanceDust(i))) continue;
        i.dust = true;
        if (reach === null) reach = guidanceDustReach(await guidanceDustExpected());
        if (!(i.distance > reach)) todo.push(i);
        else if (!dustLeftSaid.has(i.id)) { dustLeftSaid.add(i.id); log(`  dust: ${i.type} @${i.distance}m left (beyond ${reach.toFixed(0)} m for ~${dustExpect.v ?? '?'} dust)`); }
      }
    }
    if (todo.length === 0) {
      // Something still on the ground that cannot be picked up yet (still falling): wait for it.
      // Iteration 18: 2 s, not 6 - run-025 stood 4 s and 6 s after two Sky fights with nothing logged (a drop
      // lands in about a second; one that stays unusable longer is not falling). Said when given up.
      const waitFor = items.filter(i => !seen.has(i.id));
      if (waitFor.length && waits++ < ITEM_WAITS) { await sleep(500); continue; }
      if (waitFor.length && waits === ITEM_WAITS + 1) log(`  left on the ground, not usable after ${ITEM_WAITS * 0.5} s: ${waitFor.map(i => `${i.type} @${i.distance}m`).join(', ')}`);
      // Last, with the room clear: a healing shrine, if the hero is hurt enough for it.
      // Iteration 31: never one that gives dust (i.dust, or the run's list says so) - no hp is expected from it.
      const heals = lull ? [] : interactables.filter(i => !seen.has(i.id) && i.kind === 'shrine' && HEAL_SHRINES[i.type] && usable(i) &&
        !i.dust && !(Array.isArray(lucidActive) && guidanceGivesDust(lucidActive, null)));
      const hpPct = hero.hp / hero.maxHp;
      // Iteration 23: next to the zone boss, any real hurt is worth the shrine; a heal-cutting curse leaves the plain one.
      const bossNext = heals.length ? await bossNextDoor() : false;
      const below = i => hpPct < Math.max(HEAL_SHRINES[i.type], bossNext ? HEAL_BEFORE_BOSS : 0);
      const cut = healCut(hero);
      const cutFor = i => cut && i.type === 'Shrine_Guidance';
      const heal = heals.find(i => i.canInteract && below(i) && !cutFor(i) && i.distance <= healReach(hpPct) && !healNothing.has(i.type));
      if (!heal) {
        for (const h of heals) { seen.add(h.id); log(`heal: ${h.type} @${h.distance}m left - hp ${Math.round(hero.hp)}/${Math.round(hero.maxHp)}${bossNext ? ' (the boss next door)' : ''}${h.canInteract ? '' : ' (cannot use it)'}${cutFor(h) ? ` (${cut.type} cuts heals)` : healNothing.has(h.type) ? ' (gave nothing before)' : !below(h) ? ' (not hurt enough)' : h.distance > healReach(hpPct) ? ' (too far)' : ''}`); }
        if (!heals.length) return;
        continue;
      }
      log(`heal: ${heal.type} @${heal.distance}m at ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp, cost ${JSON.stringify(heal.details.shrine.cost)}`);
      todo.push(heal);
    }
    // Iteration 53: the well after the room's other loot (a memory or essence picked up, a dust shard, a merchant) - so it
    // judges the dust with what the room brought (run-052: Primus's start-room well spent 505 dust at 3.5-5.1/dust, then
    // Stygian Rush came, 20.2/dust, with 24 left). Nearest first otherwise.
    const isWell = i => i.type === 'Shrine_UpgradeWell';
    const it = todo.sort((a, b) => isWell(a) - isWell(b) || (a.distance ?? 99) - (b.distance ?? 99))[0];
    // Not through a burning pool, nor while still on fire (waitOutFire): run-006 lost 181, 197 and
    // ~530 hp walking to deposits and shrines across a Fire Elemental's pools after the fight.
    if (it.position) { await waitOutFire(it.position, 'going for ' + (it.name || it.type)); if (await enemyNear()) { log(abortLine()); return; } }
    seen.add(it.id); triedHere.add(it.id);
    // In LavaLand: there by a way off the lava, or not at all (walkSafe).
    if (it.position && !(await walkSafe(it.position, it.name || it.type))) { if (abortWhy) { log(abortLine()); return; } continue; }
    // Iteration 16: the loot after the fight (not a lull in one) dashes along its longer walks (walkDash); a
    // deposit is broken from the attack range, so the walk to it ends that much short.
    if (WALK_DASHES && !lull && it.position && it.distance > 9) await walkDash(it.position, hero, it.kind === 'prop' ? LOOT_DASH_LEFT + 8 : LOOT_DASH_LEFT, it.name || it.type);
    log('interact', it.kind, it.type, it.name || '', `@${it.distance}m`);
    if (it.type === 'Shrine_BossSoul') { soulUsedHere = true; await tryPost('/hero/interact', { id: it.id }); await soulUpgrade(); continue; }
    if (it.type === 'Shrine_UpgradeWell') { await tryPost('/hero/interact', { id: it.id }); await upgradeAt(it); continue; }
    if (it.kind === 'prop') { await breakProp(it); continue; }
    if (it.kind === 'merchant') { await shop(it); continue; }
    if (HEAL_SHRINES[it.type]) { if (it.dust) await useDustShrine(it); else await useHealShrine(it); continue; }
    // A memory or essence that costs health: picked up, a memory goes straight into a free slot -
    // run-005's Immolation did - so break it where it lies (the map will not travel with it there).
    if ((it.kind === 'memory' || it.kind === 'essence') && COSTS_HEALTH.has(it.type)) {
      log('  dismantle it on the ground - it costs health on every cast');
      await tryPost('/hero/dismantle', { id: it.id });
      continue;
    }
    // Iteration 25: an essence is picked up unless the choice for it in hand would be dust (planHeld: dead everywhere
    // with every socket but R's taken and nothing weaker parked, or only its drawback left) - then broken where it lies.
    // One of a type already socketed: picking it up merges the two (Gem.OnInteract -> MergeGem) - always taken.
    const heroNow = it.kind === 'essence' ? await get('/hero') : null;
    if (it.kind === 'essence' && !(heroNow.skills || []).some(k => (k.gems || []).some(g => g.type === it.type))) {
      const fit = await heroFit({ id: it.id });
      const p = fit ? planHeld(heroNow, fit, { type: it.type }) : null;
      if (p && p.act === 'dismantle') {
        log(`  dismantle it on the ground - ${p.why === 'harm' ? 'dead everywhere, only its drawback left' : p.why === 'only R free' ? 'dead everywhere, only R has a free socket' : 'every socket taken, nothing weaker parked'} | ${fitLine(fit)}`);
        await tryPost('/hero/dismantle', { id: it.id });
        continue;
      }
    }
    await tryPost('/hero/interact', { id: it.id });
    if (it.kind === 'shrine') await useShrine(it);
    else await waitGone(it.id, it.type);
  }
}

// A healing shrine (HEAL_SHRINES): onto it first, then use it, then stand there while the heal
// comes (explodeDelay, then over ticks) - the use alone gave nothing in 7 tries (run-004, run-005).
// Logged: hp before, the most it reached, how far the hero stood, its effects. A type that gives
// nothing this way goes into healNothing and is left for the rest of the run.
// run-007 (iter-6): a Guidance stood on 0.6 m off, 5 s watched: nothing, no heal effect, the shrine
// no longer available. Its own numbers (pure reads of the shrine, Shrine_Guidance's fields) say why:
// an explodeDelay longer than the watch (the heal goes to whoever is in `range` when it goes off),
// or a range the hero was not in. The watch now lasts explodeDelay + 2 s (5-15 s), and the numbers
// are logged with the result.
async function shrineNumbers(id) {
  const f = async p => { try { const v = await get('/reflect/get', { path: `#${id}.${p}` }); return typeof v === 'number' ? v : null; } catch { return null; } };
  const [explodeDelay, healRatio, ticks, tickInterval, radius, healDelayByDistance] = await Promise.all(['explodeDelay', 'healRatio', 'ticks', 'tickInterval', 'range.radius', 'healDelayByDistance'].map(f));
  return { explodeDelay, healRatio, ticks, tickInterval, radius, healDelayByDistance };
}

// --- heal range (iteration 23) ---
// Where a DewCollider really is (history/DewCollider.cs PositionColliders / UpdateProxyCollider): a 2D proxy at the
// transform's (x, z), turned by -yaw, scaled by lossyScale (x, z); a circle's offset and radius are in that frame
// (the 2D circle takes the larger scale), a box's offset and size too, a polygon's points. r: the raw reads
// { pos, scale, euler, offset, shape, radius, size, points }. Answers { centre: {x, z}, radius, shape } - radius
// being how far from the centre the hero is surely inside - or null when the transform could not be read.
function healRangeShape(r) {
  if (!r || !r.pos || typeof r.pos.x !== 'number' || typeof r.pos.z !== 'number') return null;
  const sx = Math.abs(r.scale && typeof r.scale.x === 'number' ? r.scale.x : 1) || 1;
  const sz = Math.abs(r.scale && typeof r.scale.z === 'number' ? r.scale.z : 1) || 1;
  const th = -((r.euler && typeof r.euler.y === 'number' ? r.euler.y : 0) * Math.PI / 180);
  const toWorld = (ox, oy) => { const x = ox * sx, y = oy * sz; return { x: r.pos.x + x * Math.cos(th) - y * Math.sin(th), z: r.pos.z + x * Math.sin(th) + y * Math.cos(th) }; };
  const shape = typeof r.shape === 'string' ? r.shape : 'Circle';
  if (shape === 'Polygon' && Array.isArray(r.points) && r.points.length >= 3) {
    const w = r.points.map(q => toWorld(q.x || 0, q.y || 0));
    const centre = { x: w.reduce((n, q) => n + q.x, 0) / w.length, z: w.reduce((n, q) => n + q.z, 0) / w.length };
    let radius = Infinity;
    for (let i = 0; i < w.length; i++) {
      const u = w[i], v = w[(i + 1) % w.length], dx = v.x - u.x, dz = v.z - u.z, L = dx * dx + dz * dz;
      const t = L ? Math.max(0, Math.min(1, ((centre.x - u.x) * dx + (centre.z - u.z) * dz) / L)) : 0;
      radius = Math.min(radius, Math.hypot(centre.x - (u.x + t * dx), centre.z - (u.z + t * dz)));
    }
    return { centre, radius, shape };
  }
  const off = r.offset || {};
  const centre = toWorld(off.x || 0, off.y || 0);
  if (shape === 'Box') {
    const size = r.size || {};
    return { centre, radius: Math.min((size.x ?? 1) * sx, (size.y ?? 1) * sz) / 2, shape };
  }
  return { centre, radius: (typeof r.radius === 'number' ? r.radius : 1) * Math.max(sx, sz), shape: 'Circle' };
}
// Where to stand: the centre, and "inside" as within HEAL_IN of the reach (or 1 m, for a tiny range).
const HEAL_IN = 0.6;
const healInside = (rg, p) => !!(rg && p && dist(p, rg.centre) <= Math.max(1, rg.radius * HEAL_IN));
// --- end heal range ---

// The shrine's range read by pure getters (the `range` field, its transform), with the interact pivot and the shrine's
// own position for the log.
async function healRange(id) {
  const g = async p => { try { return await get('/reflect/get', { path: `#${id}.${p}` }); } catch { return null; } };
  const names = ['range.transform.position', 'range.transform.lossyScale', 'range.transform.eulerAngles', 'range.offset', 'range.shape', 'range.radius', 'range.size', 'range.points', 'interactPivot.position', 'position'];
  const [pos, scale, euler, offset, shape, radius, size, points, pivot, at] = await Promise.all(names.map(g));
  const rg = healRangeShape({ pos, scale, euler, offset, shape, radius, size, points });
  const v = q => q && typeof q.x === 'number' ? { x: q.x, z: q.z } : null;
  return rg ? { ...rg, pivot: v(pivot), at: v(at) } : null;
}

// Whether the zone boss is one travel away (a heal shrine is then worth it at almost any hurt - HEAL_BEFORE_BOSS).
async function bossNextDoor() {
  try { const map = await get('/map'); return map.nodes.some(n => n.type === 'ExitBoss' && n.reachable); } catch { return false; }
}

// A heal shrine in the boss room itself, before the boss (Room_Sky_Boss_0's start plateau has a Shrine of Guidance -
// the notes of run-008): used below HEAL_BEFORE_BOSS, within HEAL_SHRINE_DIST, with no enemy about.
async function bossRoomHeal() {
  try {
    const hero = await get('/hero');
    if (hero.hp / hero.maxHp >= HEAL_BEFORE_BOSS) return;
    const cut = healCut(hero);
    const cands = (await get('/interactables', { radius: healWorked ? HEAL_SHRINE_DIST : HEAL_TRY_DIST })).interactables.filter(i => i.kind === 'shrine' && HEAL_SHRINES[i.type] && i.canInteract &&
      i.details && i.details.shrine && i.details.shrine.available && !i.details.shrine.locked && !((i.details.shrine.cost && i.details.shrine.cost.gold || 0) > hero.gold) &&
      !healNothing.has(i.type) && !(cut && i.type === 'Shrine_Guidance'));
    // Iteration 31: not one that gives dust instead (guidanceDust) - no health to be had from it before the boss.
    let h = null;
    for (const c of cands) { if (!(await guidanceDust(c))) { h = c; break; } }
    if (!h) { if (cands.length) log(`heal: ${cands[0].type} @${cands[0].distance}m in the boss room left - it gives dream dust, not health (${GUIDANCE_DUST_DREAM})`); return; }
    if ((await get('/entities', { kind: 'enemies', radius: 30, limit: 1 })).entities.length) { log(`heal: ${h.type} @${h.distance}m in the boss room left - enemies about`); return; }
    log(`heal: ${h.type} @${h.distance}m in the boss room, before the boss, at ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp`);
    lootAbort = 12;
    try { await useHealShrine(h); } finally { lootAbort = 0; }
  } catch (e) { if (e && e.dead) throw e; log('  boss-room heal:', e.message); }
}

// Iteration 23: into the range, use, hold still. run-004/005/007/009/013/016/031 all measured the hero against the
// interactable's position and some then walked (the interact) - none was ever healed, the blessed one included (it
// sets health to max outright: nothing to miss had the hero been inside). Now: the range's own centre and reach
// (healRange); a walk there; the use (/hero/interact walks to the interact pivot - up to 3 m off it); the moment
// the shrine stops being available is the use, and the burst comes explodeDelay later: if the use left the hero
// outside, one step back to the centre, then no move at all until Se_GenericHealOverTime shows or hp rises, or
// explodeDelay + HEAL_HOLD s. Logged: where it stood against the range at the use and at the burst.
const HEAL_HOLD = 1.5;   // iteration 24: was 3 (the heal lands explodeDelay + ~0.02 s/m after the use, or never)
// Iteration 24: what the burst depends on (Shrine_Guidance.OnUse's Routine: a coroutine on the shrine that waits
// explodeDelay, then range.GetEntities(includeUncollidable) - the DewCollider's 2D proxy - and heals each entity whose
// owner.isHumanPlayer), read by pure getters: whether the shrine and its range are still active (a coroutine dies with
// its GameObject), whether the range has its proxy, an actionOverride (replaces the heal), the use counts, the parent
// (a room modifier's shrine), and the hero's side of the filter. A compact "k=v" string; '?' where a read failed.
async function healProbe(id) {
  const rd = async p => { try { const v = await get('/reflect/get', { path: p }); return v === null || typeof v === 'object' ? (v && v.$type ? v.$type : JSON.stringify(v)) : v; } catch { return '?'; } };
  const names = { go: `#${id}.gameObject.activeInHierarchy`, act: `#${id}.isActive`, rEn: `#${id}.range.enabled`, rGo: `#${id}.range.gameObject.activeInHierarchy`,
    proxy: `#${id}.range._proxy.enabled`, ovr: `#${id}.actionOverride.Count`, used: `#${id}.totalUseCount`, max: `#${id}.maxTotalUseCount`,
    parent: `#${id}.parentActor.name`, human: '$hero.owner.isHumanPlayer', heroAct: '$hero.isActive', uncol: '$hero.Status.hasUncollidable' };
  const keys = Object.keys(names), vals = await Promise.all(keys.map(k => rd(names[k])));
  let out = keys.map((k, i) => k + '=' + vals[i]).join(' ');
  // proposals/iter-24-mod.md, once applied: the range's own entity query and the burst's trace.
  if (!healRoutesMissing) {
    try { out += ' | /shrine/range ' + JSON.stringify(await call('GET', `/shrine/range?id=${id}`)); }
    catch (e) { if (/no route/i.test(e.message)) healRoutesMissing = true; else out += ' | /shrine/range: ' + String(e.message).split('\n')[0].slice(0, 100); }
  }
  return out;
}
let healRoutesMissing = false;
async function healTrace() {
  if (healRoutesMissing) return '';
  try { return ' | /heals/trace ' + JSON.stringify(await call('GET', '/heals/trace?limit=12')); } catch (e) { return ''; }
}
async function useHealShrine(it) {
  const h0 = await get('/hero');
  const cfg = await shrineNumbers(it.id);
  const rg = await healRange(it.id);
  const centre = rg ? rg.centre : it.position;
  const fmt = q => q ? `(${q.x.toFixed(1)}, ${q.z.toFixed(1)})` : '?';
  log(`  heal: range ${rg ? `${rg.shape} r ${rg.radius.toFixed(1)} at ${fmt(rg.centre)}` : "not read - the shrine's own spot"}; the shrine at ${fmt(it.position)}, its interact pivot ${fmt(rg && rg.pivot)}; delay ${cfg.explodeDelay ?? '?'}s`);
  if (!centre) return;
  // Off the lava in LavaLand (walkSafe), as every other loot walk.
  if (!(await walkSafe(centre, 'a heal shrine'))) return;
  await tryPost('/hero/move', { x: centre.x, z: centre.z, wait: true, timeout: 2 + dist(h0.position, centre) / 4 });
  const avail = async () => { try { return await get('/reflect/get', { path: `#${it.id}.isAvailable` }); } catch { return null; } };
  await tryPost('/hero/interact', { id: it.id });
  // The use: the shrine no longer available.
  const t = Date.now();
  let usedAt = null, again = false;
  while (Date.now() - t < 6000) {
    if (await enemyNear()) return;
    let a = await avail();
    if (a !== true && a !== false) {   // not read: the listing says
      const now = (await get('/interactables', { radius: 60 })).interactables.find(x => x.id === it.id);
      a = !!(now && now.details && now.details.shrine && now.details.shrine.available);
    }
    if (a === false) { usedAt = Date.now(); break; }
    if (!again && Date.now() - t > 2500) { again = true; await tryPost('/hero/interact', { id: it.id }); }
    await sleep(100);
  }
  if (!usedAt) { healNothing.add(it.type); log(`  heal: ${it.type} not used in 6 s${again ? ' (interacted twice)' : ''} - not tried again this run`); return; }
  const probeUse = await healProbe(it.id);
  let h = await get('/hero');
  const atUse = h.position;
  let stepped = false;
  const delayMs = (cfg.explodeDelay ?? 1) * 1000;
  if (rg && !healInside(rg, h.position) && Date.now() - usedAt < delayMs - 200) {
    stepped = true;
    await tryPost('/hero/move', { x: centre.x, z: centre.z, wait: true, timeout: Math.max(0.6, (delayMs - (Date.now() - usedAt)) / 1000) });
  }
  // Hold still: no move from here on.
  let best = h.hp, atBurst = null, fxAt = null, rose = null, probeBurst = null;
  const until = usedAt + delayMs + HEAL_HOLD * 1000;
  for (let i = 0; Date.now() < until; i++) {
    await sleep(150);
    h = await get('/hero');
    if (!atBurst && Date.now() - usedAt >= delayMs) atBurst = h.position;
    if (!probeBurst && Date.now() - usedAt >= delayMs + 250) probeBurst = await healProbe(it.id);
    best = Math.max(best, h.hp);
    if (!fxAt && (h.statusEffects || []).some(e => e.type === 'Se_GenericHealOverTime')) fxAt = Date.now();
    if (!rose && best - h0.hp >= Math.max(5, 0.02 * h0.maxHp)) rose = Date.now();
    if (fxAt || rose) break;
    if (i % 3 === 2 && await enemyNear()) return;
  }
  atBurst = atBurst || h.position;
  const gained = Math.round(best - h0.hp);
  const rel = q => rg ? `${dist(q, rg.centre).toFixed(1)}m from the range centre (r ${rg.radius.toFixed(1)})` : `${dist(q, it.position).toFixed(1)}m from the shrine`;
  const fx = (h.statusEffects || []).map(e => e.type).filter(x => !/^Se_Star_|^Se_Hero/.test(x));
  log(`  heal: ${gained > 0 ? '+' + gained : 'nothing'} (${Math.round(h0.hp)} -> ${Math.round(h.hp)}/${Math.round(h.maxHp)})${fxAt ? `, Se_GenericHealOverTime ${((fxAt - usedAt) / 1000).toFixed(1)}s after the use` : ''}${rose ? `, hp up ${((rose - usedAt) / 1000).toFixed(1)}s after it` : ''}; at the use ${rel(atUse)}${stepped ? ', stepped back in' : ''}, at the burst ${rel(atBurst)}${it.position ? ', ' + dist(atBurst, it.position).toFixed(1) + 'm from the shrine' : ''}; fx ${fx.join(',') || '-'} | shrine: heal ${cfg.healRatio ?? '?'} of max in ${cfg.ticks ?? '?'} x ${cfg.tickInterval ?? '?'}s, delay by distance ${cfg.healDelayByDistance ?? '?'}`);
  log(`  heal: probe at the use ${probeUse} | after the burst ${probeBurst || (await healProbe(it.id))}${await healTrace()}`);
  if (fxAt || rose) healWorked = true;
  if (!fxAt && !rose) { for (const t of Object.keys(HEAL_SHRINES)) healNothing.add(t); log(`  heal: ${it.type} gave nothing ${rg && healInside(rg, atBurst) ? 'standing inside its range' : 'and the hero was not inside its range at the burst'} - no heal shrine used again this run`); }
}

async function soulUpgrade() {
  let open = false;
  for (let i = 0; i < 40 && !open; i++) {
    const st = await get('/state');
    open = st.edit && st.edit.mode === 'EditSkillShrine';
    if (!open) await sleep(250);
  }
  if (!open) { log('  the soul did not open the edit screen'); return; }
  // Iteration 37: the upgrade that adds most a minute (a memory's +1 level or an essence's +50 quality - free here), by
  // what each memory and essence does in fights (upgradeCands); until iteration 36 R first, then the rarest memory.
  const hero = await get('/hero');
  const ges = await upgradeGes();
  const rows = await impactRows(hero);
  const cands = upgradeCands(hero, rows, { ges, dq: IMP.SOUL_DQ, dl: IMP.SOUL_DL });
  // Iteration 53: the best gain x keep (a W/E memory's level is lost when it is replaced), never a junk memory while
  // anything else gains (soulPick; runs 049-056 gave R 10 of the 28 souls).
  const order = soulPick(cands, hero, dustCtx());
  let done = false;
  for (let i = 0; i < 4; i++) {
    const best = order[i];
    if (!best) break;
    const r = await tryPost('/edit/click', best.kind === 'gem' ? { slot: best.slot, index: best.index } : { slot: best.slot });
    if (r.error || (r.refused && r.refused.length)) { log(`  refused (${best.name}):`, r.error || r.refused.join('; ')); continue; }
    log(`  boss soul: upgrade ${best.kind === 'gem' ? `${best.slot} ${best.index} ${best.type} (q${best.quality ?? '?'})` : `${best.slot} ${best.type} (lvl ${best.level})`} - ${upgradeLine(best, cands, true)}${junkSay({ junk: dustRate(cands, hero, dustCtx()).filter(x => x.junk) }, hero).replace("no dust", "no soul")} | ${impactLine(rows)}`);
    done = true;
    break;
  }
  if (!done) {
    // Nothing valued or every click refused: the old order (R, then the rarest).
    const R = { Legendary: 4, Epic: 3, Rare: 2, Common: 1, Character: 0 };
    const order = ['R', 'E', 'W', 'Q'].map(sl => hero.skills.find(k => k.slot === sl)).filter(k => k && k.type)
      .sort((a, b) => (b.slot === 'R') - (a.slot === 'R') || (R[b.rarity] || 0) - (R[a.rarity] || 0));
    for (const k of order) {
      const r = await tryPost('/edit/click', { slot: k.slot });
      if (r.error || (r.refused && r.refused.length)) { log('  refused:', r.error || r.refused.join('; ')); continue; }
      log(`  boss soul: upgrade ${k.slot} ${k.type} (lvl ${k.level}) - the old order (${upgradeLine(null, cands, true)})`);
      break;
    }
  }
  await sleep(500);
  if ((await get('/state')).edit) await tryPost('/edit/end');
  await sleep(1500);   // the gold and dust it bursts into
}

async function upgradeAt(well) {
  // Walking there opens the edit screen on arrival.
  let open = false;
  for (let i = 0; i < 40 && !open; i++) {
    const st = await get('/state');
    open = st.edit && st.edit.mode === 'EditSkillShrine';
    if (!open) await sleep(250);
  }
  if (!open) { log('  the well did not open'); return; }
  let lastKey = null;   // iteration 20: run-029 clicked '+4 for 80 dust (have 110)' three times, nothing changing
  // Iteration 37: each click on the upgrade that adds most a minute per dust - a memory's level or a socketed essence's
  // quality (the well's edit screen takes a click on a socket as it takes one on a skill: Shrine_UpgradeWell's
  // OnActivateEditSkill for a GemLocation) - among what the dust pays for; until iteration 36 R, W, E, Q in that order.
  const ges = await upgradeGes();
  const refused = new Set();
  // Iteration 53: each click only when it clears the dust budget's bar (dustJudge) - else the dust is saved, said in one line.
  const ctx = dustCtx();
  for (let i = 0; i < 16; i++) {
    const hero = await get('/hero');
    const rows = await impactRows(hero);
    const { memCost, gemCost } = await upgradeCosts(hero);
    const cands = upgradeCands(hero, rows, { memCost, gemCost, ges, dq: ges.dq ?? IMP.GEM_DQ, dl: 1 }).filter(c => !refused.has(`${c.slot}:${c.index ?? ''}`));
    const j = dustJudge(cands, hero.dreamDust, hero, ctx);
    let best = j.buy ? j.buy.c : null;
    // No memory valued at all (no stats, no numbers): in the final zone the old order rather than dust left for nothing.
    if (!best && ctx.final && cands.filter(c => c.kind === 'mem').every(c => !(c.gain > 0)))
      best = ['R', 'W', 'E', 'Q'].map(sl => cands.find(c => c.kind === 'mem' && c.slot === sl && c.cost > 0 && c.cost <= hero.dreamDust)).find(Boolean) || null;
    if (!best) { log(`  well: ${dustLine(j, hero.dreamDust, hero)}${i ? '' : ` | ${upgradeLine(null, cands)}`}`); break; }
    const key = `${best.slot}:${best.index ?? ''}:${best.kind === 'gem' ? best.quality : best.level}:${hero.dreamDust}`;
    if (key === lastKey) { log(`  upgrade ${best.name}: the last click changed nothing (${best.kind === 'gem' ? 'q' + best.quality : 'lvl ' + best.level}, ${hero.dreamDust} dust) - leaving the well`); break; }
    lastKey = key;
    const r = await tryPost('/edit/click', best.kind === 'gem' ? { slot: best.slot, index: best.index } : { slot: best.slot });
    if (r.error || (r.refused && r.refused.length)) { log(`  refused (${best.name}):`, r.error || r.refused.join('; ')); refused.add(`${best.slot}:${best.index ?? ''}`); continue; }
    log(`${j.buy ? dustLine(j, hero.dreamDust, hero) : `dust: upgrade ${best.name} for ${best.cost} - the old order (no memory valued; the final zone)`} | dust ${hero.dreamDust} -> ${hero.dreamDust - best.cost} | upgrade: ${upgradeLine(best, cands)}${i === 0 ? ` | ${impactLine(rows)}` : ''}`);
    await sleep(500);
    const st = await get('/state');
    if (!st.edit) break;   // wells that close after one use
  }
  if ((await get('/state')).edit) await tryPost('/edit/end');
}

// ----- travelling ---------------------------------------------------------------------------
let fights = 0;          // rooms where there was something to fight
let bossSeenHere = false;   // a zone boss (monsterType Boss) was seen in this room - boss_down needs it
// Whether this room has been counted in `fights`: once per room. fight() runs again after each
// "enemies turned up while looting", and each run counted the room anew - run-004's zone-1 node 7
// counted 3, so the route took 2 fights fewer than it meant to.
let roomCounted = false;
// The boss soul already used by the loot after the fight: the wait for it below is skipped (run-010 z1
// and run-011 z0 waited 12 s for a soul already used - "boss soul: not seen after 12.3s").
let soulUsedHere = false;
const droppedHere = new Set();   // iteration 47: memories this room's replaces put down (not taken back here)
const triedHere = new Set();   // ids loot() has used or picked up in this room (travel: unclaimed rewards)

// Rooms between two map nodes (ZoneManager.GetNodeDistance, a pure getter), read once per zone
// and pair: the map does not change within a zone. Read a dozen at a time.
// One way only: the matrix is not symmetric. ZoneManager.CalculateDistance (decompiled) sets every
// distance FROM the exit node to 10000 - the way leads out of the boss, never through it - so
// D(boss, x) is 10000 while D(x, boss) is the real count. Keyed by the smaller index first, every
// node numbered above the boss read 10000 to it: run-002's zone 0 (boss = node 3) travelled with
// "10005 rooms from there to the boss" and the route through the fights was chosen blind.
const nodeDistances = new Map();
async function distanceTable(zone, pairs) {
  const key = (a, b) => `${zone}:${a}:${b}`;
  const todo = [...new Set(pairs.filter(([a, b]) => a !== b).map(([a, b]) => key(a, b)))].filter(k => !nodeDistances.has(k));
  for (let i = 0; i < todo.length; i += 12) {
    await Promise.all(todo.slice(i, i + 12).map(async k => {
      const [, a, b] = k.split(':').map(Number);
      try { nodeDistances.set(k, await post('/reflect/call', { path: 'ZoneManager.instance.GetNodeDistance', args: [a, b] })); } catch { }
    }));
  }
  return (a, b) => a === b ? 0 : nodeDistances.get(key(a, b)) ?? 99;
}

// Room modifiers on the world map (iter-6 mod proposal): a node `shows` one when /map lists it with
// visible === true - only what the map shows the player. Without `visible` (the mod not applied)
// nothing shows and the route is as before.
const shows = (n, re) => (n.modifiers || []).some(md => md && md.visible === true && re.test(md.type || ''));
const AURA_HP = 0.6;   // below this share of max health, keep out of an Aura of Pain room
// Iteration 41: the well rooms (RoomMod_SpawnWell: the room's Shimmering Well; decompiled ZoneManager: a modifier the zone puts on
// combat nodes, one a zone in the runs). run-049 entered Despair with 647 dust: each zone's one well came in its first combat room
// and was spent to the last upgrade (23, 15, 23 dust left); the dust after it (Guidance shrines under SparklingDreamFlask,
// +85/+106, and ~220 from the Ink boss's room) waits for the next zone's well (run-050: 901 dust in Sky until its well in the last
// combat room before the boss) - and run-049 died in Despair's first room with
// it. A reachable well node the map shows is taken with WELL_TRIP_DUST unspent when it costs no room over the fights' way,
// with WELL_ROOM_DUST when it costs one (~4 upgrades at zone 2-3's 60-100 a level), never two. Whether the map shows the
// wells at all is logged once a zone ("map: a well shown at node ..." / "no well room shown", with those not shown yet). Pure.
const WELL_MOD = /RoomMod_SpawnWell/, WELL_TRIP_DUST = 150, WELL_ROOM_DUST = 350;
const wellSaid = { zone: null };
// Iteration 53: `dust` is what the dust budget would spend there now (dustNow's plan), not all the hero holds - saved dust
// is no reason to walk; `held` (all of it) for the log.
function wellTrip(dust, extra = 0, held = dust) {
  const d = held !== dust ? `${dust} of ${held} dust to spend` : `${dust ?? '?'} dust`;
  if (!(dust >= WELL_TRIP_DUST)) return { go: false, why: `${d} (a well room is worth a trip from ${WELL_TRIP_DUST})` };
  if (extra >= 2 || (extra >= 1 && dust < WELL_ROOM_DUST)) return { go: false, why: `it costs ${extra} room${extra > 1 ? 's' : ''} over the fights' way, ${d} (${extra >= 2 ? 'never worth two' : `one from ${WELL_ROOM_DUST}`})` };
  return { go: true, why: `a well room, ${d} (${extra} room${extra === 1 ? '' : 's'} over the fights' way)` };
}

// Fights wanted per zone before heading for the boss. iter-1..9: 6. run-010 (iter-8's route) reached the
// bosses at 94%, 93% and 84% hp (lvl 4, 7, 10) and killed them in 20-44 s; a room is ~35-45 s of the
// zone's clock. iter-10: 4, and a room off the straight way must bring 2 fights in zone 2 too (it kept 1
// while Nyx was unbeaten: run-009 z2 went 4 rooms over the straight way, the sims 2.4). On game-like
// maps (tests/route.test.mjs) 6 fights at 1 a room: 6.1 rooms before the boss; 6 at 2: 4.3; 4 at 2: 4.0
// (straight 3.7).
const MIN_FIGHTS = 4;
async function travel(acceptLeftovers = false, minFights = MIN_FIGHTS) {
  if (isDead(await get('/state').catch(() => null))) throw new Dead();
  const map = await get('/map');
  const boss = map.nodes.find(n => n.type === 'ExitBoss');
  const reachable = map.nodes.filter(n => n.reachable);
  if (!reachable.length) return null;

  // Until enough fights, the shortest walk to the boss that takes them in (route.mjs), never the
  // boss itself - as long as the fights are worth the walk (chooseRoute: k fights for at most k
  // rooms more than the straight way); then the boss.
  let pick = null, why = '';
  const hero0 = await get('/hero');
  // Room modifiers the world map shows (/map modifiers' `visible`, proposals/iter-6-mod.md; without
  // it nothing here changes). Hurt, keep out of an Aura of Pain room (run-005's death: ~1% of max
  // health a second until the room is clear, stronger monsters, 2 minibosses) while another way is
  // open. (Not toward Guidance rooms: run-007's Guidance, stood on, healed nothing - iter-7 NOTES.)
  const hpPct = hero0.hp / hero0.maxHp;
  // Iteration 22: in LavaLand at any health - run-033 went into one at 424/424 (Combat_4_2: 712 taken, 283 of it the aura, 424 ->
  // 20 before a level-up) and met Infernus at 66/560; run-005 died in one there. Its fires do not stop for the aura.
  const auraOut = hpPct < AURA_HP || onLavaZone();
  const avoid = new Set(auraOut ? map.nodes.filter(n => shows(n, /AuraOfPain/)).map(n => n.index) : []);
  const ok = n => !avoid.has(n.index);
  const leftOut = reachable.filter(n => !ok(n)).map(n => n.index);
  if (leftOut.length) log(`  hp ${Math.round(100 * hpPct)}%${onLavaZone() ? ' in LavaLand' : ''}: node ${leftOut.join(', ')} shows an Aura of Pain - kept out of while another way is open`);
  const fresh = map.nodes.filter(n => n.type === 'Combat' && n.status !== 'HasVisited' && !n.current && ok(n));
  // A merchant only when it is on the way: a room nearer the boss than this one. run-006 z0 took a
  // dead-end shop (12 -> 9 -> 12 -> boss, ~19 s), run-007 z0 went 11 -> 10 (shop) -> 11 -> 12 (~36 s),
  // run-008 z1 a shop beside the boss room (6 -> 3 -> boss, ~20 s).
  // Iteration 38: and only with the gold to buy there (shopTrip: Jonas's cheapest item in this zone, not a flat 150 - run-047
  // went to zone 3's merchant with 164, his cheapest 251), and what it costs the fights' way counted (route.mjs shopDetour):
  // a merchant room holds no fight, and after 5 of the 10 shopping trips in runs 030-047 the route went a room over.
  let shopNext = reachable.find(n => n.type === 'Merchant' && n.status !== 'HasVisited' && ok(n));
  if (shopNext) {
    const t0 = shopTrip(hero0.gold, map.zoneIndex, 0);
    if (!t0.go) { log(`  merchant at node ${shopNext.index} left: ${t0.why}`); shopNext = null; }
  }
  if (shopNext && boss && typeof map.current === 'number') {
    const D = await distanceTable(map.zoneIndex, [[map.current, boss.index], [shopNext.index, boss.index]]);
    const dShop = D(shopNext.index, boss.index), dHere = D(map.current, boss.index);
    if (!(dShop < dHere)) { log(`  merchant at node ${shopNext.index} left: ${dShop} rooms from the boss, ${dHere} from here - not on the way`); shopNext = null; }
  }
  if (shopNext && boss) {
    const cands0 = reachable.filter(n => n.type !== 'ExitBoss' && ok(n)).map(n => n.index);
    const F = fights < minFights ? fresh.map(f => f.index) : [];
    const pairs = [];
    for (const a of cands0.concat(F)) { for (const f of F) pairs.push([a, f]); pairs.push([a, boss.index]); }
    const D = await distanceTable(map.zoneIndex, pairs);
    const direct = Math.min(...reachable.map(n => D(n.index, boss.index)));
    const det = shopDetour({ reachable: cands0, shop: shopNext.index, fresh: F, boss: boss.index, need: Math.max(0, minFights - fights), D, direct, perRoom: 2 });
    const trip = shopTrip(hero0.gold, map.zoneIndex, Math.max(0, det.extra));
    if (trip.go) { pick = shopNext; why = trip.why; }
    else log(`  merchant at node ${shopNext.index} left: ${trip.why} (${det.via} rooms to the boss through it${det.took ? ` taking ${det.took} fights` : ''}, ${det.best} the other way)`);
  } else if (shopNext) { pick = shopNext; why = shopTrip(hero0.gold, map.zoneIndex, 0).why; }
  // Iteration 41: a well room the map shows (wellTrip), the dust to spend there, and the room it costs the fights' way.
  const finalZone = map.zone === FINAL_ZONE || isFinalZone(lastRoom);
  if (wellSaid.zone !== map.zoneIndex) {
    wellSaid.zone = map.zoneIndex;
    const shown = map.nodes.filter(n => shows(n, WELL_MOD)).map(n => n.index);
    const listed = map.nodes.filter(n => (n.modifiers || []).some(md => md && WELL_MOD.test(md.type || ''))).length;
    log(`  map: ${shown.length ? `a well shown at node ${shown.join(', ')}` : 'no well room shown'}${listed > shown.length ? ` (${listed - shown.length} not shown yet)` : ''} - ${hero0.dreamDust ?? '?'} dust`);
  }
  const wellNext = !pick && boss && !finalZone ? reachable.find(n => n.status !== 'HasVisited' && !n.current && n.type !== 'ExitBoss' && shows(n, WELL_MOD) && ok(n)) : null;
  if (wellNext) {
    const cands0 = reachable.filter(n => n.type !== 'ExitBoss' && ok(n)).map(n => n.index);
    const F = fights < minFights ? fresh.map(f => f.index) : [];
    const pairs = [];
    for (const a of cands0.concat(F)) { for (const f of F) pairs.push([a, f]); pairs.push([a, boss.index]); }
    const D = await distanceTable(map.zoneIndex, pairs);
    const direct = Math.min(...reachable.map(n => D(n.index, boss.index)));
    // shopDetour counts the well node as the fight it is when it is a fresh combat node.
    const det = shopDetour({ reachable: cands0, shop: wellNext.index, fresh: F, boss: boss.index, need: Math.max(0, minFights - fights), D, direct, perRoom: 2 });
    const wt = wellTrip(hero0.dreamDust >= WELL_TRIP_DUST ? (await dustNow(hero0)).plan.spend : hero0.dreamDust, Math.max(0, det.extra), hero0.dreamDust);
    if (wt.go) { pick = wellNext; why = wt.why; }
    else log(`  well room at node ${wellNext.index} left: ${wt.why} (${det.via} rooms to the boss through it, ${det.best} the other way)`);
  }
  // Iteration 27: the final zone (3 nodes, all next to each other) is walked in order - its boss room is entered by the
  // door of node 1 (Shrine_PrimusDoor), not from the map.
  if (!pick && (map.zone === FINAL_ZONE || isFinalZone(lastRoom))) {
    const next = reachable.filter(n => n.type !== 'ExitBoss' && n.status !== 'HasVisited' && !n.current).sort((a, b) => a.index - b.index)[0];
    if (next) { pick = next; why = 'the final zone, in order'; }
  }
  const cands = (reachable.some(n => n.type !== 'ExitBoss' && ok(n)) ? reachable.filter(ok) : reachable).filter(n => n.type !== 'ExitBoss').map(n => n.index);
  if (!pick && fights < minFights && fresh.length && cands.length) {
    const F = fresh.map(f => f.index), b = boss ? boss.index : null;
    const pairs = [];
    for (const a of cands.concat(F)) { for (const f of F) pairs.push([a, f]); if (b !== null) pairs.push([a, b]); }
    const D = await distanceTable(map.zoneIndex, pairs);
    const need = minFights - fights;
    const direct = b === null ? 0 : Math.min(...reachable.map(n => D(n.index, b)));
    // A room off the straight way for 2 fights at least (route.mjs perRoom) - every zone since iter-10.
    const c = chooseRoute({ reachable: cands, fresh: F, boss: b, need, D, direct, perRoom: 2 });
    if (c && c.node !== null) {
      pick = map.nodes[c.node];
      why = `${c.dFresh} from a fresh fight, ${c.cost} rooms from there to the boss taking ${c.took} more (${fights}/${minFights} fights${c.took < need ? `, ${need - c.took} left out: too far off the way` : ''}; ${c.extra} rooms over the straight way)`;
    } else if (c) log(`  the fights left (${fights}/${minFights}) are too far off the way: ${c.extra} rooms more than the ${direct} to the boss for one - going to the boss`);
  }
  if (!pick) {
    const D = await distanceTable(map.zoneIndex, boss ? reachable.map(n => [n.index, boss.index]) : []);
    let best = null;
    for (const n of reachable.some(ok) ? reachable.filter(ok) : reachable) {
      const dBoss = boss ? D(n.index, boss.index) : 99;
      if (!best || dBoss < best.dBoss) best = { n, dBoss };
    }
    pick = best.n; why = `${best.dBoss} from the boss`;
  }
  log(`travel -> node ${pick.index} (${pick.type}, ${pick.status}), ${why} | hp ${Math.round(hero0.hp)}/${Math.round(hero0.maxHp)}${burning(hero0) ? ', burning' : ''}`);
  const room0 = (await get('/state')).room, exitId = room0.exitId;
  const exitPos = vecOf(room0.exitPosition);
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  await readHits();   // what came before is not this walk's
  // Iteration 35: Despair's exit on another island - over by its jump shrines first (despairHop, up to 3).
  if (isDespair(room0) && exitPos) {
    for (let k = 0; k < 3; k++) {
      const np = await get('/nav/path', { x: exitPos.x, z: exitPos.z }).catch(() => null);
      if (!np || !np.destination || dist(np.destination, exitPos) <= 4) break;
      if (!(await despairHop(exitPos, 'the exit', hopState, false))) break;   // iteration 52: toward the exit only - no fight runs here
    }
  }
  await tryPost('/hero/interact', { id: exitId });
  let shown = false, hp0 = null, steps = 0;
  for (let i = 0; i < 60 && !shown; i++) {
    const st = await get('/state');
    // Iteration 34: dead on the way (run-043 burned at the campfire and ended "stuck", without its last hits).
    if (isDead(st)) throw new Dead();
    // Iteration 34: never stand in what hurts on the way: a hit or an hp drop while walking to the exit - stop, step out
    // of its source (a fire the hits have just taught notePools, or away from the hit's `from`), round it, use it again.
    const hits = await readHits();
    const me = st.hero && st.hero.position, hp = st.hero && typeof st.hero.hp === 'number' ? st.hero.hp : null;
    const hurt = hp !== null && hp0 !== null && hp < hp0 - 0.5;
    if (hp !== null) hp0 = hp;
    if (me && (hits.length || hurt) && steps < 4) {
      const r = hitStep(me, hits, pools, Date.now(), exitPos);
      if (r) {
        steps++;
        log(`  the exit's walk: ${Math.round(hits.reduce((s, h) => s + (h.amount || 0), 0))} taken (${[...new Set(hits.map(h => h.by))].join(', ') || 'hp ' + Math.round(hp)}), ${dist(me, r.fire.centre).toFixed(1)}m from ${r.src} - stepping out to (${r.p.x.toFixed(1)}, ${r.p.z.toFixed(1)}), then the exit again`);
        await tryPost('/hero/move_dir', { x: 0, z: 0 });
        await tryPost('/hero/move', { x: r.p.x, z: r.p.z, wait: true, timeout: 3 });
        if (exitPos) await walkSafe(exitPos, 'the exit');
        await tryPost('/hero/interact', { id: exitId });
        hp0 = null;
        continue;
      }
    }
    // Iteration 27: a rift with its next room set (Rift_RoomExit.nextNodeIndex: "move to the next location?") travels
    // without the map - answered Yes by handleBlocking, then the room changes under us.
    if (st.loading || (st.room && st.room.node !== room0.node)) {
      await post('/flow/wait_playing', { timeout: 60 });
      const now = (await get('/state')).room || {};
      log(`  the rift took us on by itself: node ${now.node} (${now.nodeType})`);
      return { index: now.node, type: now.nodeType, status: 'HasVisited' };
    }
    if (await handleBlocking(st)) continue;
    shown = (await get('/map')).worldMapShown === 'Shown';
    if (!shown) await sleep(250);
  }
  if (!shown) {
    if (isDead(await get('/state').catch(() => null))) throw new Dead();
    log('the world map did not open at the exit'); return null;
  }
  const r = await tryPost('/map/travel', { node: pick.index });
  if (r.error || (r.refused && r.refused.length)) { log('travel refused:', r.error || r.refused.join('; ')); return null; }
  for (let i = 0; i < 8; i++) {
    const st = await get('/state');
    // "You have unclaimed rewards": going back has never collected anything - the list was empty
    // (run-001 twice, run-003, run-004) or held a shrine the loot had already used (Chaos in
    // run-003, Corrupted Chaos in run-004: offer taken, still listed), and cost 2-8 s each time.
    // So go back only for a reward not yet tried in this room and not one the loot leaves on
    // purpose (SKIP_SHRINES, healing shrines kept for when hurt); else answer Yes and go.
    const untried = st.message && (st.room && st.room.unclaimed || []).filter(u => !triedHere.has(u.id) && !SKIP_SHRINES.has(u.type) && !HEAL_SHRINES[u.type] && !(/^Shrine_/.test(u.type || '') && !GOOD_SHRINES.has(u.type)));
    if (!acceptLeftovers && st.message && /unclaimed/i.test(st.message.text) && !untried.length) {
      log('the map says:', st.message.text.split(String.fromCharCode(10))[0], '- nothing there not tried already, going on:', JSON.stringify((st.room && st.room.unclaimed || []).map(u => u.type + ' #' + u.id)));
    } else if (!acceptLeftovers && st.message && /unclaimed|left behind|on the ground/i.test(st.message.text)) {
      log('the map says:', st.message.text.split(String.fromCharCode(10))[0], '- going back for it:', JSON.stringify((st.room && st.room.unclaimed || []).map(u => u.type + ' #' + u.id)));
      const no = st.message.buttons.find(b => b.button === 'No' || b.button === 'Cancel');
      await tryPost('/message/answer', { button: no ? no.button : 'Cancel' });
      return 'loot again';
    }
    if (!(await handleBlocking(st))) break;
  }
  await post('/flow/wait_playing', { timeout: 60 });
  return pick;
}

// Through the boss room's rift into the next zone, as a player does: walk up, use it, answer.
// run-007: the first try failed in both boss rooms ("the exit did not take us", 30 s each): one
// interact, then 30 s of waiting - the rift was not open or not usable yet when it was used. Now it
// waits for the exit to be open and uses it again every 5 s while still in the zone.
// Iteration 27: rift - another rift to use instead of the exit (the Dream rift to the final boss, findDreamRift);
// acceptLeftovers - go even if "You have unclaimed rewards" lists something the loot has not tried (else: 'loot again').
async function nextZone(rift = null, acceptLeftovers = false) {
  let st0 = await get('/state');
  for (let i = 0; i < 20 && st0.room && !st0.room.exitOpen; i++) { await sleep(500); st0 = await get('/state'); }
  const riftId = rift ? rift.id : st0.room.exitId;
  // And unlocked: the rifts stay locked until the boss soul is used (see auto()), then unlock
  // 0.25 s apart after its reward drops.
  const exitLocked = async () => { try { const x = (await get('/interactables', { radius: 200 })).interactables.find(i => i.id === st0.room.exitId); return !!(x && x.details && x.details.locked); } catch { return false; } };
  for (let i = 0; i < 25 && await exitLocked(); i++) await sleep(400);
  const zone = st0.room.zoneIndex;
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  await tryPost('/hero/interact', { id: riftId });
  const t = Date.now();
  let usedAt = Date.now();
  while (Date.now() - t < 30000) {
    const st = await get('/state');
    // Iteration 27: in a boss room the "unclaimed rewards" question came after every soul (runs 030-040) and was
    // answered Yes. Now what it lists is logged, and something the loot has not tried sends the bot back for it.
    if (st.message && /unclaimed/i.test(st.message.text || '')) {
      const all = (st.room && st.room.unclaimed) || [];
      const left = untriedRewards(all, triedHere, SHRINE_SETS);
      log(`the rift says: ${st.message.text.split(String.fromCharCode(10))[0]} ${JSON.stringify(all.map(u => u.type + ' #' + u.id))}${left.length && !acceptLeftovers ? ' - going back for ' + left.map(u => u.type).join(', ') : ' - nothing to go back for, going on'}`);
      if (left.length && !acceptLeftovers) {
        const no = st.message.buttons.find(b => b.button === 'No' || b.button === 'Cancel');
        await tryPost('/message/answer', { button: no ? no.button : 'Cancel' });
        return 'loot again';
      }
    }
    if (await handleBlocking(st)) continue;
    if (st.room && st.room.zoneIndex !== zone && st.uiState === 'Playing' && !st.loading) {
      await post('/flow/wait_playing', { timeout: 60 });
      return true;
    }
    if (!st.loading && st.uiState === 'Playing' && st.room && st.room.zoneIndex === zone && Date.now() - usedAt > 5000) {
      usedAt = Date.now();
      await tryPost('/hero/interact', { id: rift ? riftId : st.room.exitId });
    }
    await sleep(400);
  }
  return false;
}

// The `dead` event with the last hits. Also from the top-level catch when a Dead comes out of the
// loot or travel (enemyNear, travel's own check) - run-011/012 died outside fight() and went on.
let lastRoom = null;   // /state.room of the room auto() is in
async function emitDead() {
  let hits = [];
  // The last 15 hits: /damage keeps the newest `limit`, but the reply's arrays stop at 200
  // entries, so asking for 400 from seq 0 gave the oldest 200 - run-004's `dead` event listed
  // zone 0-1 hits for a zone-2 death.
  // Iteration 19: only this zone's (zoneHitSeq, read as the zone began) - the game keeps /damage across runs, and
  // run-027's dead event (Skoll) listed run-026's Little Baam, Baam and Nyx Starfall hits (metrics: "Starfall/Nyx 270").
  try { hits = (await get('/damage', { since: Math.max(0, zoneHitSeq), limit: 15 })).hits.filter(h => h && h.by && typeof h.amount === 'number'); } catch { }
  const r = lastRoom || {};
  emit('dead', { zone: r.zoneIndex, room: r.room, nodeType: r.nodeType,
    lastHits: hits.map(h => ({ by: h.by, caster: h.caster, amount: Math.round(h.amount), overTime: h.overTime })) });
}

// --- cycle (iteration 27) ---
// The game's run, decompiled (history/it27, NOTES iteration 27). A cycle is 4 zones, one per tier (the build's content
// settings "Content - Release (2026 June)": zoneCountByTier [1, 1, 1, 1]): z0 Forest, z1 SnowMountain | DarkCave |
// LavaLand, z2 Sky | Ink, z3 Despair (boss Azurak). In the boss room of the cycle's last zone (zoneIndex % 4 == 3)
// RoomRifts adds Rift_Sidetrack_TheDream beside the exit, 1.8 s after the rifts open: it asks "meet the beginning" and
// travels to Zone_Primus (zone 4 - tier -1, never drawn by the tiers; 3 nodes: Start_0, Combat_0 with Shrine_PrimusDoor,
// Boss_0 with Mon_Primus_BossPrimusAeron). The exit itself asks "dream again" and starts the next cycle (Forest,
// loopIndex 1): never taken. Primus down -> Primus_Ending.StartPrimusDeath (the creeps destroyed, ~12 s of white fade,
// the hero moved to the threshold, dazed, skills locked); the hero uses Shrine_PrimusEndingLightPillar -> the ending
// cutscene -> GameManager.ConcludePureWhiteDream: gameConcluded, the Result screen (DewGameResult PureWhiteDream).
const CYCLE_ZONES = 4, FINAL_ZONE = 'Zone_Primus', DREAM_RIFT = 'Rift_Sidetrack_TheDream', PRIMUS_DOOR = 'Shrine_PrimusDoor', ENDING_PILLAR = 'Shrine_PrimusEndingLightPillar';
// The Result screen is a death - unless the final boss went down first (the ending's own Result screen).
const deadState = (st, ended) => !!st && ((st.uiState === 'Result' && !ended) || !!(st.hero && st.hero.knockedOut));
const isFinalZone = room => !!room && room.zone === FINAL_ZONE;
// The cycle's last zone: its boss room holds the rift to the final boss.
const isCycleEnd = room => !!room && !isFinalZone(room) && (room.zone === 'Zone_Despair' || (!room.zone && room.zoneIndex % CYCLE_ZONES === CYCLE_ZONES - 1));
// Primus down: in its room, seen there, and nothing of the creeps' alive (StartPrimusDeath destroys them all; through its
// phase changes it stays alive at 1 hp - Se_Mon_Primus_BossPrimusAeron_PhaseSwitcher's death interrupt).
const finalBossGone = (room, bossSeen) => isFinalZone(room) && room.nodeType === 'ExitBoss' && !!bossSeen && room.enemiesAlive === 0;
// "You have unclaimed rewards": what is worth going back for - not what the loot left on purpose (healing shrines kept
// for when hurt, shrines not known to give anything, ones tried already). The boss soul always is.
const untriedRewards = (unclaimed, tried, { skip, heal, good }) => (unclaimed || []).filter(u => u.type === 'Shrine_BossSoul' ||
  (!tried.has(u.id) && !skip.has(u.type) && !heal[u.type] && !(/^Shrine_/.test(u.type || '') && !good.has(u.type))));
// After a boss (settleDrops): what lies around - items and shrines (with whether they can be used yet) and pickups. A change
// (a drop landing, the soul turning usable, gold flying in) starts the quiet over.
const dropsKey = r => [...((r && r.interactables) || []).filter(i => i.kind !== 'rift' && i.kind !== 'merchant').map(i => `i${i.id}${i.canInteract ? '+' : ''}`),
  ...((r && r.pickups) || []).map(p => `p${p.id}`)].sort().join(',');
// One look of the wait: 'soul' (usable - the loot takes it now), 'quiet' (nothing new for quietMs, the soul seen when
// wanted), 'cap', or null (wait on). s: { t0, key, changedAt, soul }.
function settleStep(s, key, soul, now, { quietMs = 1750, capMs = 9000, needSoul = true } = {}) {
  if (s.key !== key) { s.key = key; s.changedAt = now; }
  if (soul) s.soul = true;
  if (soul && soul.canInteract && needSoul) return 'soul';
  if ((s.soul || !needSoul) && now - s.changedAt >= quietMs) return 'quiet';
  if (now - s.t0 >= capMs) return 'cap';
  return null;
}
// --- end cycle ---

let finalDown = null;   // { t } once the final boss is down (iteration 27): the Result screen after it is the victory
const SHRINE_SETS = { skip: SKIP_SHRINES, heal: HEAL_SHRINES, good: GOOD_SHRINES };
// The boss's drops, let settle before the loot and the walk to the rift (iteration 27): in every boss room of runs
// 030-040 the loot ran at once, used the soul, and the exit then said "You have unclaimed rewards" - the soul comes ~4 s
// after the boss, its burst and the drops after that. The hero stands still meanwhile, capMs at most.
async function settleDrops(what, opts = {}) {
  const s = { t0: Date.now(), key: null, changedAt: Date.now(), soul: false };
  let why = null;
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  // Iteration 43: not stood in the Ink ground while it still ticks (3 s after the clear): out of it first (run-046 stood the
  // wait 7.1 m from its centre - 4 ticks).
  const h0 = await get('/hero').catch(() => null);
  const gp = h0 && h0.position ? groundOnWay(pools, Date.now(), h0.position, null) : null;
  if (gp) {
    const d = Math.max(0.1, dist(h0.position, gp.centre)), k = (gp.radius + 1.5) / d;
    log(`  drops after ${what}: in the damaging ground (${d.toFixed(1)}m from its centre, r ${gp.radius.toFixed(1)}, ${((gp.until - Date.now()) / 1000).toFixed(1)}s more) - out of it while they settle`);
    await tryPost('/hero/move', { x: gp.centre.x + (h0.position.x - gp.centre.x) * k, z: gp.centre.z + (h0.position.z - gp.centre.z) * k });
  }
  while (!why) {
    await enemyNear();   // death, and in LavaLand the lava
    const r = await get('/interactables', { radius: 70 }).catch(() => null);
    const soul = r && (r.interactables || []).find(i => i.type === 'Shrine_BossSoul');
    why = settleStep(s, dropsKey(r), soulUsedHere ? null : soul, Date.now(), { needSoul: !soulUsedHere, ...opts });
    if (!why) await sleep(300);
  }
  log(`  drops after ${what}: ${why === 'soul' ? 'the soul is usable' : why === 'quiet' ? `nothing new for ${((opts.quietMs || 1750) / 1000).toFixed(1)}s` : 'waited the most'} (${((Date.now() - s.t0) / 1000).toFixed(1)}s${s.soul ? ', soul seen' : ''})`);
  return why;
}

// The rift to the final boss in the cycle's last boss room (it comes 1.8 s after the rifts open).
async function findDreamRift(maxMs = 20000) {
  const t = Date.now();
  while (Date.now() - t < maxMs) {
    const r = await get('/interactables', { radius: 200 }).catch(() => null);
    const x = r && (r.interactables || []).find(i => i.type === DREAM_RIFT);
    if (x && !(x.details && x.details.locked)) { log(`  the rift to the final boss (${DREAM_RIFT}) @${x.distance}m after ${((Date.now() - t) / 1000).toFixed(1)}s`); return x; }
    await sleep(500);
  }
  return null;
}

// The final zone's door room: knock (Shrine_PrimusDoor) - the hero is dazed and stunned, a cutscene plays, then the boss
// room loads (ZoneManager.LoadNode to node 2).
// Iteration 35: over to another island of a Despair room by its jump shrine (despairPick) - the landing read by pure getters
// (Shrine_Despair.targetPos.position, Shrine_Despair_Vestige.destination), the walk to the shrine checked on /nav/path.
// Uses it (as F does) and waits out the flight (Se_Shrine_Despair_Teleport on the hero: invulnerable, dazed, ~delay +
// displaceDuration). True when the hero flew.
const isDespair = room => !!room && room.zone === 'Zone_Despair';
const hopState = { used: new Map(), lastAt: 0, saidNone: 0, heldWhy: null, heldAt: 0, foot: null, footDone: new Set() };
const footKey = (room, p) => `${(room && room.room) || '?'}:${Math.round(p.x)}:${Math.round(p.z)}`;
// Iteration 48: hopGate before the walk ("despair: hop held - ..."), hopDanger on each look of it ("despair: hop aborted -
// ..."). A part of the room still to fight on foot: hs.foot = { p, at } - fight()'s quiet branch walks there first.
async function despairHop(goal, what, hs = hopState, withOthers = true) {
  if (!goal || Date.now() - hs.lastAt < 3000) return false;
  hs.lastAt = Date.now();
  const [r, st, h0, en] = await Promise.all([get('/interactables', { radius: 90 }).catch(() => null), get('/state').catch(() => null),
    get('/hero').catch(() => null), get('/entities', { kind: 'enemies', radius: 90, limit: 40 }).catch(() => null)]);
  if (isDead(st)) throw new Dead();
  const me = st && st.hero && st.hero.position;
  if (!me) return false;
  const all = ((r && r.interactables) || []).filter(i => DESPAIR_SHRINE.test(i.type || ''));
  const list = all.filter(i => i.canInteract && i.position);
  const cands = [];
  for (const i of list.slice(0, 8)) {
    const [dest, np] = await Promise.all([peek(i.type === 'Shrine_Despair' ? `#${i.id}.targetPos.position` : `#${i.id}.destination`).catch(() => null),
      get('/nav/path', { x: i.position.x, z: i.position.z }).catch(() => null)]);
    const reach = np && np.destination && dist(np.destination, i.position) <= 3 && typeof np.length === 'number' ? np.length : null;
    cands.push({ id: i.id, type: i.type, position: i.position, dest: vecOf(dest), reach, usedAt: hs.used.get(i.id) || 0 });
  }
  // Iteration 52: the room's other parts still to fight (despairPickAny), while it is neither cleared nor open.
  const room0 = (st && st.room) || {};
  const others = !withOthers || room0.exitOpen || room0.cleared === true ? [] : [...(room0.combatAreas || []).filter(c => c && !c.active), ...(room0.clearsOnEnter || [])].map(c => vecOf(c.position || c)).filter(Boolean);
  const pick0 = despairPickAny(me, goal, others, cands, Date.now());
  if (!pick0) {
    if (Date.now() - hs.saidNone > 15000) { hs.saidNone = Date.now(); log(`  despair: no path to ${what} (${dist(me, goal).toFixed(1)}m off) and no jump shrine that helps - ${all.length} in sight, ${list.length} usable: ${cands.map(c => `${c.type} #${c.id} ${c.reach == null ? 'unreachable' : c.reach.toFixed(0) + 'm walk'} -> ${c.dest ? dist(c.dest, goal).toFixed(1) + 'm from it' : 'landing unknown'}`).join('; ') || '-'}${others.length ? ` (nor toward the room's ${plural(others.length, 'other part', 'other parts')} still to fight)` : ''}`); }
    return false;
  }
  const pick = pick0;
  if (pick.other) { what = `a part of the room still to fight (${pick.goal.x.toFixed(1)}, ${pick.goal.z.toFixed(1)}) (none helps toward ${what}, ${dist(me, goal).toFixed(1)}m off)`; goal = pick.goal; }
  // Iteration 48: the gate. The enemies near the hero and near the landing; a part still to fight reachable on foot.
  const foes = ((en && en.entities) || []).filter(e => e && e.position);
  const nearOf = p => foes.filter(e => dist(e.position, p) <= HOP_CLEAR).length;
  const hpFrac = h0 && h0.maxHp ? h0.hp / h0.maxHp : 1;
  let foot = null;
  const room = (st && st.room) || {};
  const areas = (room.combatAreas || []).map(c => vecOf(c.position)).filter(p => p && dist(p, goal) > 4 && dist(p, me) > 3 && !hs.footDone.has(footKey(room, p))).sort((p, q) => dist(me, p) - dist(me, q)).slice(0, 4);
  for (const p of areas) {
    const np = await get('/nav/path', { x: p.x, z: p.z }).catch(() => null);
    if (np && np.destination && dist(np.destination, p) <= 4 && typeof np.length === 'number') { foot = { p, len: np.length }; break; }
  }
  const g = hopGate({ hpFrac, near: nearOf(me), landingNear: nearOf(pick.dest), foot: foot ? foot.len : null, reach: pick.reach });
  if (!g.go) {
    if (g.foot) hs.foot = { p: foot.p, at: Date.now() };
    if (hs.heldWhy !== g.why || Date.now() - hs.heldAt > 10000) { hs.heldWhy = g.why; hs.heldAt = Date.now(); log(`  despair: hop held - ${g.why} (the jump shrine ${pick.type} #${pick.id}, ${pick.reach.toFixed(1)}m walk, toward ${what} ${dist(me, goal).toFixed(1)}m off; ${plural(room.enemiesAlive || 0, 'enemy', 'enemies')} alive in the room) | hp ${h0 ? Math.round(h0.hp) : '?'}`); }
    return false;
  }
  hs.heldWhy = null; hs.foot = null;
  log(`  despair: no path to ${what} (${dist(me, goal).toFixed(1)}m off) - the jump shrine ${pick.type} #${pick.id} (${pick.reach.toFixed(1)}m walk) lands ${dist(pick.dest, goal).toFixed(1)}m from it${g.flee ? ` - ${g.why}` : ''} (nobody within ${HOP_CLEAR} m; ${plural(room.enemiesAlive || 0, 'enemy', 'enemies')} alive in the room) | hp ${h0 ? Math.round(h0.hp) : '?'}`);
  hs.used.set(pick.id, Date.now());
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  await readHits();   // what came before is not this walk's
  await tryPost('/hero/interact', { id: pick.id });
  const t = Date.now();
  let flew = false, h = null, sentAt = t, lastPos = null, stillSince = 0, fleeSaid = false;
  while (Date.now() - t < 15000) {
    h = await get('/hero').catch(() => null);
    const on = !!h && (h.statusEffects || []).some(e => /Shrine_Despair_Teleport/.test(e.type || ''));
    if (on) flew = true;
    if (h && !on && (flew || dist(h.position, pick.dest) < 4)) break;
    if (!flew && Date.now() - t > 9000) break;   // never got there (still walking, refused)
    if (isDead(await get('/state').catch(() => null))) throw new Dead();
    // Iteration 48: on the way (not yet in flight - invulnerable then): hits, enemies, red, shots - stop and fight.
    if (!flew && h && h.position) {
      const [hits, e2, th] = await Promise.all([readHits(), get('/entities', { kind: 'enemies', radius: HOP_CLEAR, limit: 20 }).catch(() => null),
        get('/threats', { radius: HOP_CLEAR }).catch(() => null)]);
      const left = dist(h.position, pick.position);
      const d = hopDanger({ hpFrac: h.maxHp ? h.hp / h.maxHp : 1, near: ((e2 && e2.entities) || []).length, landingNear: nearOf(pick.dest), left, hits, threats: th });
      if (d && d.stop) {
        await tryPost('/hero/stop');
        hs.used.delete(pick.id);   // not used: free to try again once it is quiet
        log(`  despair: hop aborted after ${((Date.now() - t) / 1000).toFixed(1)}s, ${left.toFixed(1)}m from the shrine - ${d.why} - back to the fight | hp ${Math.round(h.hp)}`);
        return false;
      }
      if (d && !fleeSaid) { fleeSaid = true; log(`  despair: ${d.why}`); }
      // Stunned or knocked back, the game's interact walk ends there: sent again when the hero has stood still 1 s.
      if (lastPos && dist(lastPos, h.position) < 0.15) { if (!stillSince) stillSince = Date.now(); } else stillSince = 0;
      lastPos = h.position;
      if (stillSince && Date.now() - stillSince > 1000 && Date.now() - sentAt > 1500 && left > 1.5) { sentAt = Date.now(); stillSince = 0; await tryPost('/hero/interact', { id: pick.id }); }
    }
    await sleep(250);
  }
  const at = h && h.position;
  log(`  despair: ${flew ? 'flew' : 'did not fly'} in ${((Date.now() - t) / 1000).toFixed(1)}s${at ? ` - now ${dist(at, goal).toFixed(1)}m from ${what}` : ''}`);
  return flew || (!!at && dist(at, me) > 8);
}

async function primusDoor() {
  const r = await get('/interactables', { radius: 200 }).catch(() => null);
  const door = r && (r.interactables || []).find(i => i.type === PRIMUS_DOOR);
  if (!door) return false;
  const node0 = ((await get('/state')).room || {}).node;
  log(`final zone: the door @${door.distance}m${door.canInteract ? '' : ' (not usable yet)'} - knocking`);
  const t = Date.now();
  let lastTry = 0;
  while (Date.now() - t < 120000) {
    const st = await get('/state');
    if (isDead(st)) throw new Dead();
    if (st.room && st.room.node !== node0 && !st.loading && st.uiState === 'Playing') { cutsceneOver(); await post('/flow/wait_playing', { timeout: 60 }); log(`  through the door in ${((Date.now() - t) / 1000).toFixed(1)}s`); return true; }
    if (await handleBlocking(st)) continue;
    if (st.uiState === 'Cutscene') { await skipCutscene(st); await sleep(500); continue; }
    if (!st.loading && st.uiState === 'Playing' && st.room && st.room.node === node0 && Date.now() - lastTry > 8000) { lastTry = Date.now(); await tryPost('/hero/interact', { id: door.id }); }
    await sleep(400);
  }
  log('  the door did not take us');
  return false;
}

// Primus down: the ending. Wait out StartPrimusDeath (~12 s), use the light pillar, let the cutscene (skipped: its end
// concludes the game either way - DewCutsceneDirector's skip runs the same stop sequence and onFinish) bring the Result.
async function ending() {
  const t0 = Date.now();
  let lastTry = 0, said = false;
  while (Date.now() - t0 < 240000) {
    const st = await get('/state').catch(() => null);
    if (!st) { await sleep(1000); continue; }
    if (st.gameConcluded || st.uiState === 'Result') {
      // The game's own word for it (a pure read; DewGameResult.ResultType: PureWhiteDream is Primus's ending).
      const kind = await peek('GameResultManager.instance.current.result');
      log(`ending: the game is concluded (${st.uiState}, result ${JSON.stringify(kind)}) ${((Date.now() - t0) / 1000).toFixed(1)}s after the final boss went down`);
      emit('ending', { seconds: Math.round((Date.now() - t0) / 100) / 10, uiState: st.uiState, result: kind ?? null });
      return 'victory';
    }
    if (await handleBlocking(st)) continue;
    if (st.uiState === 'Cutscene') { await skipCutscene(st); await sleep(500); continue; }
    cutsceneOver();
    if (st.loading || st.uiState !== 'Playing') { await sleep(500); continue; }
    const r = await get('/interactables', { radius: 200 }).catch(() => null);
    const pillar = r && (r.interactables || []).find(i => i.type === ENDING_PILLAR);
    if (pillar && pillar.canInteract && Date.now() - lastTry > 6000) {
      lastTry = Date.now();
      log(`ending: the light pillar @${pillar.distance}m - using it`);
      await tryPost('/hero/interact', { id: pillar.id });
    } else if (!said && Date.now() - t0 > 20000) { said = true; log(`ending: ${pillar ? 'the light pillar is not usable' : 'no light pillar listed'} after 20 s`); }
    await sleep(500);
  }
  log('ending: the game did not conclude in 240 s');
  return 'final boss down - the ending did not conclude';
}

let zoneSeen = null;
let zoneHitSeq = -1;   // /damage's last seq as the zone began (emitDead)
let lavaCfgSaid = false;   // the lava's numbers logged (iteration 14)
// Iteration 27: on through every zone of the cycle and the final boss (was: stop after zone 2). Ends with 'victory', a
// death, or a dead end ('stuck ...', 'cleared zones 0..N - ...').
async function auto(maxRooms = 80) {
  // Iteration 31: the run's lucid dreams, once (a resume is a new process: read again).
  if (lucidActive === null) await readLucid();
  for (let room = 0; room < maxRooms; room++) {
    const st = await get('/state');
    if (isDead(st)) throw new Dead();
    if (st.room) { lastRoom = st.room; zoneName = st.room.zone || ''; }
    if (st.room) {
      if (st.room.zoneIndex !== zoneSeen) {
        zoneSeen = st.room.zoneIndex;
        const t = Date.now();   // the clock starts now, not after the map is read
        try { zoneHitSeq = (await get('/damage', { limit: 1 })).last; } catch { }
        // The map's size and the shortest way to the boss, to judge the route against.
        let nodes = null, bossRooms = null;
        try {
          const map = await get('/map');
          const boss = map.nodes.find(n => n.type === 'ExitBoss');
          nodes = map.nodes.length;
          if (boss) bossRooms = (await distanceTable(map.zoneIndex, [[map.current, boss.index]]))(map.current, boss.index);
        } catch { }
        log(`zone ${st.room.zoneIndex}: ${nodes} nodes, the boss ${bossRooms} rooms from here`);
        // Iteration 27: which cycle (ZoneManager.loopIndex, a pure read) - 0 until a "dream again" exit is taken.
        const loop = await peek('ZoneManager.instance.loopIndex');
        emit('zone', { t, zone: st.room.zoneIndex, name: st.room.zone, nodes, bossRooms, loop: typeof loop === 'number' ? loop : null });
      }
      emit('room', { zone: st.room.zoneIndex, room: st.room.room, node: st.room.node, nodeType: st.room.nodeType, fights });
    }
    // Hits read since the last "took" line (the loot's lava checks, the fire waits, a fight's last look) are
    // the room behind us: said here, not in the next room's first fight line (run-019 logged a Belphomet
    // missile in LavaLand a minute after the boss died).
    await readHits();
    if (Object.keys(tookAcc).length) log('  took before this room:', tookLine());
    lavaHit.at = 0; lavaHit.recent.length = 0;
    roomCounted = false; triedHere.clear(); droppedHere.clear(); soulUsedHere = false; pools.length = 0; fireWaited = 0; groundHeld.said = false; groundHeld.stopped = false; bossSeenHere = false; bossEntry = null; blind.hits = 0; blind.redAt.length = 0; blind.queue.length = 0; shield.far = []; shield.said = null; shield.targetId = null; shield.pastSaid = null; shield.lavaWay.clear(); shield.lavaAt.clear();   // pools are the last room's
    bh.centre = undefined; bh.polled = null; bh.said = null; bhHit.first = bhHit.last = 0; bhHit.at = null; checkedHere.clear(); merchantMemo.clear(); merchantSaid.clear();
    Object.assign(cata, { on: false, safe: null, key: null, spot: null, said: null, waves: 0, moving: false }); Object.assign(grounds, { findAt: 0, refs: [], readAt: 0, list: [], said: false });
    log(`=== zone ${st.room && st.room.zoneIndex} ${st.room && st.room.zone}: ${st.room && st.room.room} (node ${st.room && st.room.node}, ${st.room && st.room.nodeType})`);
    // Iteration 34: the room's campfire, a fixed fire from the start (fight, loot, the exit's walk) - not from its first burn.
    const fps = await readFireplaces();
    for (const f of fps) addFireplace(pools, f, Date.now());
    if (fps.length) log(`  campfire: ${fps.map(f => `(${f.centre.x.toFixed(1)}, ${f.centre.z.toFixed(1)}) radius ${f.radius ?? '?'} - kept ${fireplaceRadius(f.radius).toFixed(1)}m off`).join('; ')}`);
    // The lava's numbers, once (iteration 14; prefab values the code does not show - how fast it ticks and
    // ramps, and how far and how often it rises and falls, which moves its edge): pure reads.
    if (onLavaZone() && !lavaCfgSaid) {
      const L = 'LavaLand_Lava.instance.';
      const names = ['tickInterval', 'damageMaxHpRatio', 'damageMultiplierPerTick', 'fireChance', 'enableTranslation', 'translationInterval', 'translationRange', 'transform.localPosition', 'transform.position'];
      const vals = await Promise.all(names.map(n => peek(L + n)));
      if (vals.some(v => v !== undefined && v !== null)) { lavaCfgSaid = true; log(`  lava: its numbers ${JSON.stringify(Object.fromEntries(names.map((n, i) => [n, vals[i]])))}`); }
    }
    let result, bossDown = false, dropsSettled = false, quietRetries = 0;   // quietRetries: iteration 52 (quietRetry)
    // Iteration 23: a heal shrine in the boss room before the boss (bossRoomHeal).
    if (st.room && st.room.nodeType === 'ExitBoss' && st.room.cleared !== true) await bossRoomHeal();
    // The boss going down is the end of the zone's clock (metrics.mjs): written when the boss room's
    // fight ends, not after its loot - run-005's came 42 s late, after a walk to a Shrine of Guidance.
    const bossDownNow = async zone => {
      bossDown = true;
      const hero = await get('/hero');
      log(`boss of zone ${zone} down - hero lvl ${hero.level}, ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp`);
      emit('boss_down', { zone, level: hero.level, hp: Math.round(hero.hp), maxHp: Math.round(hero.maxHp), fights });
    };
    // Iteration 27: the final boss down - the zone's clock ends as any zone's, then the ending.
    const finalBossDownNow = async room => {
      finalDown = { t: Date.now() };
      if (!bossDown) await bossDownNow(room.zoneIndex);
      emit('final_boss_down', { zone: room.zoneIndex, name: room.zone });
    };
    for (let round = 0; round < 8; round++) {
      result = await fight();
      if (result === 'final boss down') {
        log('fight: the final boss is down');
        await finalBossDownNow(st.room);
        return await ending();
      }
      // With the hp and whether it burns: run-006 lost 181-586 hp between "clear" and the next room,
      // and nothing logged the hp in between.
      const hc = result === 'dead' ? null : await get('/hero').catch(() => null);
      log('fight:', result + (hc ? ` - hp ${Math.round(hc.hp)}/${Math.round(hc.maxHp)}${burning(hc) ? ', burning' : ''}${pools.length ? `, ${pools.length} fire pools known` : ''}` : ''));
      const bossRoom = st.room && st.room.nodeType === 'ExitBoss';
      // The boss down: the fight ended with the exit open and the boss was seen here (or the game says
      // the room is cleared). run-007's zone 2 wrote boss_down after "quiet but exit closed" - Nyx
      // never showed - and ended the run "cleared zones 0..2".
      if (result === 'clear' && !bossDown && bossRoom) {
        const cleared = bossSeenHere || ((await get('/state')).room || {}).cleared === true;
        if (cleared) await bossDownNow(st.room.zoneIndex);
        else log('  the exit is open but no boss was seen here - not counted as the boss down');
      }
      if (result === 'dead') { await emitDead(); return 'dead'; }
      // Iteration 27: the boss's drops come in over the next seconds (the soul ~4 s after it, then its burst) - let them.
      if (bossRoom && bossDown && !dropsSettled && !isFinalZone(st.room)) { dropsSettled = true; await settleDrops('the boss'); }
      // A boss room not done (quiet, the exit shut, or a timeout): look for the boss again, no loot.
      if (bossRoom && !bossDown && result !== 'clear') { log(`  the boss room is not done (${result}) - looking for the boss again`); continue; }
      lootAbort = 12; await loot(); lootAbort = 0;
      if (!(await get('/entities', { kind: 'enemies', radius: 30, limit: 1 })).entities.length) {
        // Iteration 52: quiet, the exit shut and the room not cleared (run-055's Ink Combat_2: a combat area cut off at the entry
        // whose way opened later) - the room's goals once more, all asked anew, before the exit and "stuck" (quietRetry).
        const rs = result === 'quiet but exit closed' ? ((await get('/state').catch(() => null)) || {}).room : null;
        if (quietRetry(result, rs, quietRetries, bossRoom, isFinalZone(rs || st.room))) {
          quietRetries++;
          const ca = (rs.combatAreas || []).filter(c => !c.active);
          log(`  the room is not cleared and its exit is shut (${plural(ca.length, 'combat area', 'combat areas')} not woken${ca.length ? ': ' + ca.map(c => { const p = c.position || c; return `(${p.x.toFixed(1)}, ${p.z.toFixed(1)})`; }).join(', ') : ''}) - the room's goals once more (${quietRetries}/${QUIET_RETRIES})`);
          continue;
        }
        break;
      }
      log('  enemies turned up while looting - back to the fight');
    }
    if (result === 'dead') return 'dead';
    // Iteration 16: a combat room that cleared with no enemy ever in sight counts as a fight all the same.
    // Such rooms carry a special-entity modifier (decompiled SpecialEntityRoomModifier: RoomMod_GiftMerchant,
    // RoomMod_DreamTeller - an NPC, dust and gold, Room.RemoveCombat(clearRoomOnEnteringLastSection: true):
    // no monsters, the room clears when the hero walks into its last part). Not counted, the route made up
    // for it with a room off the straight way: run-022 z1 node 7 -> "1 rooms over" (~33 s), run-015, run-014
    // the same, run-018 "1 left out"; one such room in 5 of the last 9 runs. Its modifiers are logged to
    // learn whether the world map shows them (then the route could leave such rooms out as fights).
    // Not a room passed through again (cleared already when entered: Room.didClearRoom = isRevisit).
    if (result === 'clear' && st.room && st.room.nodeType === 'Combat' && st.room.cleared !== true && !roomCounted) {
      roomCounted = true; fights++;
      let mods = '?';
      try { const map = await get('/map'); const n = map.nodes.find(x => x.index === map.current); mods = ((n && n.modifiers) || []).map(m => `${m.type}${m.visible ? ' (shown)' : ' (hidden)'}`).join(', ') || 'none'; } catch { }
      log(`  no enemy seen in this combat room - counted as a fight all the same (${fights}/${MIN_FIGHTS}); the node's modifiers: ${mods}`);
    }
    const after = await get('/state');
    if (after.room && after.room.nodeType === 'ExitBoss') {
      if (!bossDown) {
        if (result === 'clear' && after.room.cleared === true) await bossDownNow(after.room.zoneIndex);
        else { log(`the boss of zone ${after.room.zoneIndex} was not found or not beaten (${result}) - stopping`); return 'stuck in the boss room'; }
      }
      // Iteration 27: the final boss's room has no next zone - its end is the ending (never its exit).
      if (isFinalZone(after.room)) { await finalBossDownNow(after.room); return await ending(); }
      let through = false;
      for (let attempt = 0; attempt < 3 && !through; attempt++) {
        // The soul, once the game lets the hero use it. Decompiled (BossMonster, Shrine_BossSoul,
        // Rift_RoomExit): the boss's death locks every rift; the soul appears 4 s later, but its
        // CanInteract is false while any BossMonster is still in the world - the dead boss stays
        // until its last attacks and projectiles are gone (Actor destroy lock, up to 60 s); using
        // it (the upgrade) makes it burst into gold and dust and unlock the rifts ~1-2 s later.
        // run-008: iter-7's "available" is true from the start, so the loot passed the soul by
        // (the loot takes only what canInteract), the exit was locked, and 30 s went by at the
        // exit both times before the second pass found the soul usable.
        const t0 = Date.now();
        let soulSeen = false, soulOk = false;
        while (Date.now() - t0 < 45000) {
          if (soulUsedHere) break;
          const s = (await get('/interactables', { radius: 100 })).interactables.find(x => x.type === 'Shrine_BossSoul');
          if (s) soulSeen = true;
          if (s && s.canInteract) { soulOk = true; break; }
          if (!s && soulSeen) break;   // gone: used already
          if (!s && Date.now() - t0 > 12000) break;   // no soul at all
          await sleep(400);
        }
        log(`  boss soul: ${soulUsedHere ? 'used already' : soulOk ? 'usable' : soulSeen ? 'listed, not usable' : 'not seen'} after ${((Date.now() - t0) / 1000).toFixed(1)}s`);
        await loot();
        // Iteration 27: the soul's burst and anything the boss dropped late, then one more loot - the walk to the rift
        // goes once, after all of it.
        await settleDrops('the soul', { needSoul: false, capMs: 6000 });
        await loot();
        // The cycle's last zone: on to the final boss by its rift, never the exit (that one starts the next cycle).
        let rift = null;
        if (isCycleEnd(after.room)) {
          rift = await findDreamRift();
          if (!rift) { log(`zone ${after.room.zoneIndex} (${after.room.zone}): no rift to the final boss (${DREAM_RIFT}) - stopping`); return `cleared zones 0..${after.room.zoneIndex} - no rift to the final boss`; }
        }
        through = await nextZone(rift);
        if (through === 'loot again') { await loot(); through = await nextZone(rift, true); }
        if (through === 'loot again') through = false;
        if (!through) log(`  the ${rift ? 'rift' : 'exit'} did not take us - looking around again`);
      }
      if (!through) { log('could not reach the next zone'); return 'stuck'; }
      fights = 0;
      continue;
    }
    // Iteration 27: the final zone's door room - on by the door (primusDoor), no exit to wait for.
    if (isFinalZone((await get('/state')).room) && await primusDoor()) continue;
    for (let i = 0; i < 20 && !(await get('/state')).room.exitOpen; i++) await sleep(500);
    const ex = (await get('/state')).room;
    if (ex && ex.exitPosition) await waitOutFire(ex.exitPosition, 'the exit');
    // In LavaLand, to the exit off the lava first (walkSafe); the exit's own walk is then short.
    // Iteration 17: no dry way - across the lava by a dash where one gets over it (crossLava), then the usual way.
    if (ex && ex.exitPosition && !(await walkSafe(ex.exitPosition, 'the exit')) && !abortWhy && onLavaZone()) { log('  the exit: no way found off the lava - crossing it'); await crossLava(ex.exitPosition, 'the exit'); }
    let went = await travel();
    if (went === 'loot again') { await loot(); went = await travel(); }
    if (went === 'loot again') { log('rewards left that the bot cannot collect'); went = await travel(true); }
    if (!went || went === 'loot again') { log('no travel'); return 'stuck'; }
  }
  return 'rooms exhausted';
}

const [cmd, arg, arg2] = process.argv.slice(2);
if (arg2) fights = +arg2;   // fights already fought, when resuming a run
const run = { auto: () => auto(+arg || 80), fight: () => fight(+arg || 240), loot, travel }[cmd];
if (!run) { console.log('usage: node tools/devbot.mjs auto [maxRooms] [fightsSoFar] | fight | loot | travel'); process.exit(1); }
// A bug in the bot should not cost the run: say what broke and carry on from where the game is.
(async () => {
  emit('start', { cmd, args: [arg, arg2].filter(Boolean) });
  for (let i = 0; i < 30; i++) {
    try { const result = await run(); log('done:', result); emit('end', { result }); return; }
    catch (e) {
      // Dead outside a fight (Dead from enemyNear / travel / auto), or an error because the hero is.
      const dead = e.dead || isDead(await get('/state').catch(() => null));
      if (dead) { log(`dead (${e.message})`); await emitDead(); log('done: dead'); emit('end', { result: 'dead' }); return; }
      log('error:', e.message, '- carrying on'); emit('error', { message: e.message }); await tryPost('/hero/move_dir', { x: 0, z: 0 }); await sleep(500);
    }
  }
})();
