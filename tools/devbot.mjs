import fs from 'fs';
// A bot that plays a run through the DevTools agent API (Debug build of DevTools, game running,
// hero in a run - /flow/start_solo gets one). It plays as a player could: only the honest routes,
// no /cheat, /reflect or /console. Per room: fight, loot (shrines, merchants, deposits, wells),
// then the world map toward the boss; through the boss's rift into the next zone; stops after the
// boss of the third zone or when the hero dies. Runs with DevTools loaded earn no progression.
//
//   node tools/devbot.mjs auto [maxRooms] [fightsSoFar]   the whole run; fightsSoFar when resuming
//   node tools/devbot.mjs fight | loot | travel            one step, in the room the hero is in
//
// Environment: DEVTOOLS_PORT (47653), DEVBOT_TRACE=<file> for a per-tick log of boss fights.
// Needs Node 18+ (fetch).
const B = 'http://127.0.0.1:' + (process.env.DEVTOOLS_PORT || 47653);
const TRACE = process.env.DEVBOT_TRACE;
const trace = line => { if (TRACE) fs.appendFileSync(TRACE, line + '\n'); };
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
const tryPost = async (p, b) => { try { return await post(p, b); } catch (e) { return { error: e.message }; } };

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

function plan(grid, me, entities, target, range, shots, areas = []) {
  const { origin, step, size, reach, clear } = grid;
  const cellPos = k => ({ x: origin.x + (k % size) * step, z: origin.z + Math.floor(k / size) * step });
  const boss = entities.find(isBossE);

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
  const desired = range * (boss ? 0.85 : 0.75);

  // Right next to one of them.
  const melee = p => {
    let d = 0;
    for (const e of entities) {
      const r = isBossE(e) ? 5.5 : 3;
      const ed = dist(p, e.position);
      if (ed < r) d += (r - ed) * (r - ed) * (isBossE(e) ? 1.5 : 2);
    }
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
  let dodge = null;
  for (const s of shots) {
    if (s.homing || s.eta > 1.0 || s.miss > s.radius + 0.8) continue;
    const ax = me.x - s.position.x, az = me.z - s.position.z;
    const cross = ax * s.heading.z - az * s.heading.x;
    const sgn = Math.abs(cross) > 0.1 ? Math.sign(cross) : orbitSide;
    dodge = { x: s.heading.z * sgn, z: -s.heading.x * sgn };
    break;
  }

  const hk = grid.hero ? grid.hero.j * size + grid.hero.i : -1;
  const cells = [];
  for (let k = 0; k < reach.length; k++) {
    if (reach[k] < 0 || reach[k] > 8) continue;
    const p = cellPos(k);
    const md = dist(me, p);
    let area = 0;
    for (const a of areas) { const dp = areaDepth(p, a); if (dp > 0) area += 10 + 6 * a.fill + 3 * dp; }
    const c = { k, p, md, clear: clear[k], reach: reach[k], melee: melee(p), line: inLine(p) + area, area };
    c.fit = -Math.abs(dist(p, C) - desired);
    c.room = 0.9 * Math.min(clear[k], 5) - (clear[k] <= 1 ? 5 : clear[k] === 2 ? 1.5 : 0);
    const v = md > 0.1 ? { x: (p.x - me.x) / md, z: (p.z - me.z) / md } : { x: 0, z: 0 };
    c.v = v;
    c.dodge = dodge ? 2.5 * (v.x * dodge.x + v.z * dodge.z) : 0;
    cells.push(c);
  }
  const here = cells.find(c => c.k === hk);
  const sideways = (c, s) => { const t = tangent(s); return c.v.x * t.x + c.v.z * t.z; };
  const walkScore = (c, s) => 2.2 * sideways(c, s) + 0.5 * c.fit + c.room - c.melee - c.line - through(c.p) + c.dodge - 0.1 * c.reach;
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
  let best = bestFor(orbitSide);
  const other = bestFor(-orbitSide);
  // The way round is blocked (a wall, a chasm, more of them): go round the other way.
  if (other && (!best || other.score > best.score + 2) && Date.now() - orbitFlippedAt > 1200) {
    orbitSide = -orbitSide; orbitFlippedAt = Date.now(); best = other;
  }

  // The first steps of the path to it: walk back down the reach gradient from the best cell.
  let way = best;
  if (best && best.reach > 3) {
    let k = best.k;
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
    way = { k, p: cellPos(k) };
  }

  // A dash lands 3.5-5.5 m away, past them if need be, where there is room and nothing next to it.
  const dashScore = c => 1.2 * Math.min(c.clear, 5) - (c.clear <= 1 ? 6 : 0) - c.melee - c.line + 1.2 * sideways(c, orbitSide) + 0.3 * c.fit + c.dodge;
  const dash = cells.filter(c => c.md > 3.5 && c.md < 5.5).sort((x, y) => dashScore(y) - dashScore(x))[0];
  return { best, way, here, dash, dodge, centre: C, inArea };
}

async function fight(maxSeconds = 300) {
  const started = Date.now();
  let quietSince = 0, lastLog = 0, sawEnemy = false;
  let lastDir = { x: 0, z: 0 }, lastDirAt = 0, lastPathAt = 0, gridCache = null, gridAt = 0;
  let lastNearbyLoot = 0, propsCache = [], propsAt = 0, lastBossD = null;
  let stuckFrom = null, stuckAt = 0;
  const stopDir = async () => { if (lastDir.x || lastDir.z) { await tryPost('/hero/move_dir', { x: 0, z: 0 }); lastDir = { x: 0, z: 0 }; } };
  while ((Date.now() - started) / 1000 < maxSeconds) {
    const [st, ents, hero, threats] = await Promise.all([
      get('/state'),
      get('/entities', { kind: 'enemies', radius: 35, limit: 30 }),
      get('/hero').catch(() => null),
      get('/threats', { radius: 25 }).catch(() => ({ projectiles: [] })),
    ]);
    if (st.uiState === 'Result') return 'dead';
    if (!st.hero || !hero) { await sleep(300); continue; }
    if (st.hero.knockedOut) return 'dead';
    if (st.hero.holding) await sortHands();
    if (await handleBlocking(st)) { await sleep(200); continue; }
    if (st.loading || st.uiState !== 'Playing') { await sleep(300); continue; }

    const entities = ents.entities;
    const room = st.room || {};
    if (entities.length === 0) {
      if (room.enemiesAlive === 0) {
        if (!quietSince) quietSince = Date.now();
        // Rooms have several combat areas that wake as the hero walks in, so only the open exit
        // says the room is done.
        if (room.exitOpen) { await stopDir(); return 'clear'; }
        if (Date.now() - quietSince > 40000) return 'quiet but exit closed';
      }
      await stopDir();
      if (!st.hero.inCombat && Date.now() - lastNearbyLoot > 5000) { lootAbort = 25; await loot(16, 6); lootAbort = 0; lastNearbyLoot = Date.now(); }
      const far = (await get('/entities', { kind: 'enemies', radius: 300, limit: 1 })).entities[0];
      // No one in sight and the exit shut: a part of the room still to fight in, or the part whose
      // entering clears the room, or the exit.
      const nearestOf = list => (list || []).map(x => x.position || x).sort((p, q) => dist(st.hero.position, p) - dist(st.hero.position, q))[0];
      const goal = far ? far.position : nearestOf((room.combatAreas || []).filter(c => !c.active)) || nearestOf(room.clearsOnEnter) || room.exitPosition;
      if (goal) await tryPost('/hero/move', { x: goal.x, z: goal.z });
      await sleep(400);
      continue;
    }
    quietSince = 0;
    if (!sawEnemy) { sawEnemy = true; fights++; }

    const me = hero.position;
    const bosses = entities.filter(isBossE);
    const target = bosses[0] || entities[0];
    const d = dist(me, target.position);
    const hpPct = hero.hp / hero.maxHp;
    const skill = s => hero.skills.find(k => k.slot === s);
    const ready = s => { const k = skill(s); return k && k.type && k.trigger && k.trigger.canCast; };
    const range = (hero.attack && hero.attack.range) || 8;
    const close = dist(me, entities[0].position);
    const shots = threats.projectiles || [];
    const areas = (threats.areas || []).map(a => a.fill < 0 ? { ...a, fill: 0.5, left: 1 } : a);

    if (Date.now() - lastLog > 3000) {
      lastLog = Date.now();
      try {
        const dmg = await get('/damage', { since: damageSeq, limit: 200 });
        if (damageSeq >= 0 && dmg.hits.length) {
          const by = {};
          for (const h of dmg.hits) { const k = (h.by || '?') + (h.caster && h.caster !== h.by ? '/' + h.caster : '') + (h.overTime ? ' (dot)' : ''); by[k] = (by[k] || 0) + h.amount; }
          log('  took', Object.entries(by).sort((x, y) => y[1] - x[1]).map(([k, v]) => Math.round(v) + ' from ' + k).join(', '));
        }
        damageSeq = dmg.last;
      } catch { }
      log(`hp ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} lvl ${hero.level} | ${entities.length} near, target ${target.name} ${Math.round(target.hp)}hp @${d.toFixed(1)}m, ${shots.length} shots, ${areas.length} red areas${areas.some(x => areaDepth(me, x, 0) > 0) ? ' (standing in one)' : ''}, circling ${orbitSide > 0 ? 'ccw' : 'cw'}`);
    }

    // Nothing within reach: walk a real path to it (a direction cannot go around a chasm).
    if (close > 30 && !st.hero.inCombat && Date.now() - lastNearbyLoot > 5000) { await stopDir(); lootAbort = 25; await loot(16, 6); lootAbort = 0; lastNearbyLoot = Date.now(); continue; }
    if (close > 12) {
      if (Date.now() - lastPathAt > 1000) { await stopDir(); await tryPost('/hero/move', { x: target.position.x, z: target.position.z }); lastPathAt = Date.now(); }
      await sleep(150);
      continue;
    }

    if (!gridCache || Date.now() - gridAt > 300) { gridCache = await get('/nav/grid', { radius: 8, step: 1 }); gridAt = Date.now(); }
    const pl = plan(gridCache, me, entities, target, range, shots, areas);
    // Standing in a red shape: how far to its edge, against how long until it lands (~5 m/s on foot).
    let areaEscape = null;
    for (const a of areas) {
      const dp = areaDepth(me, a, 0.4);
      if (dp > 0 && (!areaEscape || a.left < areaEscape.left)) areaEscape = { depth: dp, left: a.left, shape: a.shape, by: a.by };
    }
    const areaUrgent = areaEscape && areaEscape.depth / 5 + 0.2 > areaEscape.left;

    // Walking and getting nowhere (a lip the grid missed, a body in the way): go round the other way.
    if (lastDir.x || lastDir.z) {
      if (!stuckFrom) { stuckFrom = me; stuckAt = Date.now(); }
      else if (dist(me, stuckFrom) > 0.8) { stuckFrom = me; stuckAt = Date.now(); }
      else if (Date.now() - stuckAt > 900) { orbitSide = -orbitSide; orbitFlippedAt = Date.now(); stuckFrom = null; log('  not moving - circling the other way'); }
    } else stuckFrom = null;

    if (bosses.length) trace(new Date().toISOString().slice(11, 23) + ' hp ' + Math.round(hero.hp) + ' boss ' + Math.round(bosses[0].hp) + ' d ' + dist(me, bosses[0].position).toFixed(1) + ' side ' + orbitSide + ' shots ' + shots.length + ' areas ' + areas.map(a => a.shape + (a.shape === 'box' ? '' : a.radius) + '@' + a.fill + (areaDepth(me, a, 0) > 0 ? '(in)' : '')).join(',') + ' dash ' + (ready('Movement') ? 'ready' : 'cd') + ' fx ' + (hero.statusEffects || []).map(e => e.type).join(','));

    // Dash when walking will not do: one of them on top of the hero, cornered, the boss close or
    // charging, a shot about to land that stepping aside cannot beat, or low on health up close.
    const cornered = pl.here && pl.here.clear <= 2 && close < 4.5;
    let bossRushing = false;
    if (bosses.length) {
      const bd = dist(me, bosses[0].position);
      bossRushing = lastBossD !== null && bd < 8 && lastBossD - bd > 1.5;
      lastBossD = bd;
    }
    const bossNear = bosses.length > 0 && dist(me, bosses[0].position) < 4.5;
    const shotNow = shots.some(s => !s.homing && s.eta < 0.3 && s.miss < s.radius + 0.4) && (bosses.length > 0 || hpPct < 0.5);
    if ((areaUrgent || close < 2.2 || cornered || bossNear || bossRushing || shotNow || (hpPct < 0.35 && close < 5)) && ready('Movement') && pl.dash && !(pl.dash.area > 0 && areaUrgent)) {
      const why = areaUrgent ? `in a ${areaEscape.shape} of ${areaEscape.by || '?'} (${areaEscape.depth.toFixed(1)}m in, lands in ${areaEscape.left}s)` : close < 2.2 ? 'on top of us' : cornered ? `cornered (room ${pl.here.clear}m)` : bossNear ? 'boss close' : bossRushing ? 'boss charging' : shotNow ? 'shot incoming' : 'low health';
      log(`  dash: ${why} -> cell with ${pl.dash.clear}m of room, ${pl.dash.md.toFixed(1)}m away`);
      const r = await tryPost('/hero/cast', { slot: 'Movement', x: pl.dash.p.x, z: pl.dash.p.z, move: false });
      if (bosses.length) trace('  dash ' + why + ' ' + JSON.stringify(r).slice(0, 160));
      lastDirAt = 0;
      continue;
    }

    // Walk toward the chosen cell along the grid's path, shooting all the while.
    if (pl.best) {
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
    for (const s of ['R', 'W', 'E', 'Q']) {
      const k = skill(s);
      if (!ready(s)) continue;
      const kRange = k.trigger.range || 9;
      if (k.trigger.aim === 'None' ? d > 8 : d > Math.max(kRange, 4) + 1.5) continue;
      if (s === 'R' && (close < 4 || (bosses.length && d < 6))) continue;
      const r = await tryPost('/hero/cast', { slot: s, target: target.id, charge: 0.3, move: false });
      if (!r.error) { casted = true; break; }
    }
    if (casted) { lastDirAt = 0; continue; }

    if (Date.now() - propsAt > 1000) {
      propsCache = (await get('/entities', { kind: 'props', radius: 10 })).entities.filter(p => p.alive && /Stone_(Gold|DreamDust|Nightmare)/.test(p.type));
      propsAt = Date.now();
    }
    const deposit = d > range * 1.05 ? propsCache.find(p => dist(me, p.position) <= range) : null;
    await tryPost('/hero/attack_in_place', { target: (deposit || target).id });
    await sleep(30);
  }
  return 'timeout';
}

// ----- looting ------------------------------------------------------------------------------
async function waitGone(id, seconds = 8) {
  const t = Date.now();
  while (Date.now() - t < seconds * 1000) {
    if (await enemyNear()) return false;
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    try { const on = await get('/reflect/get', { path: `#${id}.owner` }); if (on !== null) return true; } catch { return true; }
    await sleep(250);
  }
  return false;
}

async function sortHands() {
  const hero = await get('/hero');
  if (!hero.holding) return;
  const item0 = hero.holding.item || {};
  if (item0.quality !== undefined) {
    // An essence: the first skill with a free socket.
    const socket = hero.skills.find(s => s.type && s.sockets > s.gems.length && ['Q', 'W', 'E', 'R'].includes(s.slot));
    if (socket) {
      const used = new Set(socket.gems.map(g => g.index));
      let index = 0; while (used.has(index)) index++;
      log('socket held', item0.type, '->', socket.slot, index);
      await tryPost('/hero/equip', { slot: socket.slot, index });
      return;
    }
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
  const weakest = own.sort((a, b) => (rank[a.rarity] ?? 0) - (rank[b.rarity] ?? 0) || a.level - b.level)[0];
  const item = hero.holding.item || {};
  if (weakest && (rank[item.rarity] ?? 0) > (rank[weakest.rarity] ?? 0)) {
    log('replace', weakest.slot, weakest.type, 'with', item.type);
    await tryPost('/hero/equip', { slot: weakest.slot });
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

// Buy what makes the hero stronger: a memory better than the weakest one worn (or one for an
// empty slot), an essence while there are free sockets. Best first, while the gold lasts.
async function shop(m) {
  await tryPost('/hero/interact', { id: m.id });
  let open = false;
  const t = Date.now();
  while (Date.now() - t < 4000 + m.distance * 500 && !open) {
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    open = st.floatingWindow && st.floatingWindow.target && st.floatingWindow.target.id === m.id;
    if (!open) await sleep(250);
  }
  if (!open) { log('  the shop did not open'); return; }
  for (let round = 0; round < 6; round++) {
    const me = (await get('/interactables', { radius: 80 })).interactables.find(x => x.id === m.id);
    const stock = (me && me.details && me.details.stock) || [];
    const hero = await get('/hero');
    const worn = hero.skills.filter(k => ['Q', 'W', 'E', 'R'].includes(k.slot));
    const emptySlot = worn.some(k => !k.type);
    const replaceable = worn.filter(k => k.type && k.rarity !== 'Character');
    const weakest = replaceable.length ? Math.min(...replaceable.map(k => RANK[k.rarity] ?? 0)) : 99;
    const freeSockets = worn.filter(k => k.type).reduce((n, k) => n + Math.max(0, k.sockets - k.gems.length), 0);
    const wants = stock.filter(x => x.count > 0 && x.price && !x.price.stardust && (x.price.gold || 0) <= hero.gold && (x.price.dreamDust || 0) <= hero.dreamDust)
      .map(x => {
        const r = RANK[x.rarity] ?? 0;
        let value = -1;
        if (x.type === 'Skill' && (emptySlot || r > weakest)) value = 10 + r * 3 + x.level;
        if (x.type === 'Gem' && freeSockets > 0) value = 5 + r * 3 + x.level / 100;
        return { x, value };
      })
      .filter(w => w.value > 0)
      .sort((a, b) => b.value - a.value);
    if (!wants.length) break;
    const buy = wants[0].x;
    log(`buy ${buy.name || buy.item} (${buy.rarity} ${buy.type}) for ${JSON.stringify(buy.price)} - have ${hero.gold} gold`);
    const r = await tryPost('/merchant/buy', { id: m.id, index: buy.index });
    if (r.error || (r.refused && r.refused.length)) { log('  refused:', r.error || r.refused.join('; ')); break; }
    await sleep(700);
    // What was bought lands at the hero's feet or in hand; put it on before choosing the next.
    await sortHands();
    for (const it of groundItems((await get('/interactables', { radius: 12 })).interactables)) {
      await tryPost('/hero/interact', { id: it.id }); await waitGone(it.id); await sortHands();
    }
  }
}

async function breakProp(p) {
  await tryPost('/hero/attack', { target: p.id });
  for (let i = 0; i < 40; i++) {
    try { if (!(await get('/reflect/get', { path: '#' + p.id + '.isAlive' }))) break; } catch { break; }
    await sleep(250);
  }
}

const groundItems = list => list.filter(i => (i.kind === 'memory' || i.kind === 'essence') && i.details && i.details.onGround && !i.details.lockedForMe);

// Use a shrine and wait for what it gives: the walk there, the use, the item falling out.
async function useShrine(it) {
  const before = new Set(groundItems((await get('/interactables', { radius: 80 })).interactables).map(i => i.id));
  const t = Date.now();
  const limit = 4000 + it.distance * 500;
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
        return named + r * 10 + (lv ? +lv[2] / 100 : 0);
      };
      const pick = offers.slice().sort((x, y) => score(y) - score(x))[0];
      log('  offers:', offers.map(o => JSON.stringify(o.offer).slice(0, 80)).join(' | '), '-> take', pick.index);
      const r = await tryPost('/shrine/choose', { id: it.id, index: pick.index });
      if (r.error || (r.refused && r.refused.length)) log('  refused:', r.error || r.refused.join('; '));
      await sleep(800);
      break;
    }

    if (choices && choices.length && windowOpen) {
      let best = 0;
      choices.forEach((c, i) => { if ((c.level || 0) > (choices[best].level || 0)) best = i; });
      log('  choices:', choices.map(c => `${c.name || c.type} +${c.level - 1}`).join(', '), '-> take', best);
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
  for (let i = 0; i < 16; i++) {
    const fresh = groundItems((await get('/interactables', { radius: 80 })).interactables).filter(x => !before.has(x.id));
    if (fresh.length) { log('  it gave', fresh.map(f => f.name || f.type).join(', ')); break; }
    await sleep(250);
  }
  if ((await get('/state')).floatingWindow) await tryPost('/ui/click', { text: 'Close' });
}

let lootAbort = 0;   // > 0: stop looting when an enemy comes this close
async function enemyNear() {
  if (!lootAbort) return false;
  const e = (await get('/entities', { kind: 'enemies', radius: lootAbort, limit: 1 })).entities;
  return e.length > 0;
}

async function loot(radius = 70, maxPasses = 30) {
  const seen = new Set();
  let waits = 0;
  for (let pass = 0; pass < maxPasses; pass++) {
    if (await enemyNear()) { log('  enemies close - back to the fight'); return; }
    await sortHands();
    const { interactables, pickups } = await get('/interactables', { radius });
    const hero = await get('/hero');
    for (const p of pickups.filter(p => p.distance > 1.5 && p.distance < Math.min(40, radius)).slice(0, 4)) {
      await tryPost('/hero/move', { x: p.position.x, z: p.position.z, wait: true, timeout: 6 });
    }
    const items = groundItems(interactables);
    const props = (await get('/entities', { kind: 'props', radius })).entities
      .filter(p => p.alive && /Stone_(Gold|DreamDust|Nightmare)/.test(p.type) && !seen.has(p.id))
      .map(p => ({ ...p, kind: 'prop', canInteract: true }));
    const merchants = interactables.filter(i => i.kind === 'merchant' && !seen.has(i.id) && hero.gold >= 60);
    const todo = props.concat(merchants, interactables.filter(i => !seen.has(i.id) && i.canInteract && (
      items.includes(i) ||
      (i.kind === 'shrine' && i.type !== 'Shrine_Guidance' && i.type !== 'Shrine_Destiny' && i.type !== 'Shrine_Stardust' && i.details && i.details.shrine && i.details.shrine.available && !i.details.shrine.locked &&
        (!i.details.shrine.cost || (i.details.shrine.cost.gold || 0) <= hero.gold) && (!(i.details.shrine.cost && i.details.shrine.cost.healthPercentage) || hero.hp / hero.maxHp > 0.7)))));
    if (todo.length === 0) {
      // Something still on the ground that cannot be picked up yet (still falling): wait for it.
      if (items.some(i => !seen.has(i.id)) && waits++ < 12) { await sleep(500); continue; }
      return;
    }
    const it = todo[0];
    seen.add(it.id);
    log('interact', it.kind, it.type, it.name || '', `@${it.distance}m`);
    if (it.type === 'Shrine_BossSoul') { await tryPost('/hero/interact', { id: it.id }); await soulUpgrade(); continue; }
    if (it.type === 'Shrine_UpgradeWell') { await tryPost('/hero/interact', { id: it.id }); await upgradeAt(it); continue; }
    if (it.kind === 'prop') { await breakProp(it); continue; }
    if (it.kind === 'merchant') { await shop(it); continue; }
    await tryPost('/hero/interact', { id: it.id });
    if (it.kind === 'shrine') await useShrine(it);
    else await waitGone(it.id);
  }
}

async function soulUpgrade() {
  let open = false;
  for (let i = 0; i < 40 && !open; i++) {
    const st = await get('/state');
    open = st.edit && st.edit.mode === 'EditSkillShrine';
    if (!open) await sleep(250);
  }
  if (!open) { log('  the soul did not open the edit screen'); return; }
  // The main damage skill first, then the rarest memory.
  const hero = await get('/hero');
  const R = { Legendary: 4, Epic: 3, Rare: 2, Common: 1, Character: 0 };
  const order = ['R', 'E', 'W', 'Q'].map(sl => hero.skills.find(k => k.slot === sl)).filter(k => k && k.type)
    .sort((a, b) => (b.slot === 'R') - (a.slot === 'R') || (R[b.rarity] || 0) - (R[a.rarity] || 0));
  for (const k of order) {
    const r = await tryPost('/edit/click', { slot: k.slot });
    if (r.error || (r.refused && r.refused.length)) { log('  refused:', r.error || r.refused.join('; ')); continue; }
    log(`  boss soul: upgrade ${k.slot} ${k.type} (lvl ${k.level})`);
    break;
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
  for (let i = 0; i < 8; i++) {
    const hero = await get('/hero');
    const order = ['R', 'W', 'E', 'Q'].map(sl => hero.skills.find(k => k.slot === sl)).filter(k => k && k.type);
    let done = false;
    for (const k of order) {
      let cost;
      try { cost = await post('/reflect/call', { path: 'GameManager.instance.GetSkillUpgradeDreamDustCost', args: ['#' + k.id] }); } catch { continue; }
      if (hero.dreamDust < cost) continue;
      const r = await tryPost('/edit/click', { slot: k.slot });
      if (r.refused && r.refused.length) { log('  refused:', r.refused.join('; ')); continue; }
      log(`upgrade ${k.slot} ${k.type} +${k.level - 1} for ${cost} dust (have ${hero.dreamDust})`);
      await sleep(500);
      done = true;
      break;
    }
    if (!done) break;
    const st = await get('/state');
    if (!st.edit) break;   // wells that close after one use
  }
  if ((await get('/state')).edit) await tryPost('/edit/end');
}

// ----- travelling ---------------------------------------------------------------------------
let fights = 0;          // rooms where there was something to fight
async function travel(acceptLeftovers = false, minFights = 6) {
  const map = await get('/map');
  const boss = map.nodes.find(n => n.type === 'ExitBoss');
  const reachable = map.nodes.filter(n => n.reachable);
  if (!reachable.length) return null;
  const distance = async (a, b) => a === b ? 0 : await post('/reflect/call', { path: 'ZoneManager.instance.GetNodeDistance', args: [a, b] });

  // Until enough fights, head for the nearest combat node not yet visited, never the boss.
  const fresh = map.nodes.filter(n => n.type === 'Combat' && n.status !== 'HasVisited' && !n.current);
  let pick = null, why = '';
  const hero0 = await get('/hero');
  const shopNext = reachable.find(n => n.type === 'Merchant' && n.status !== 'HasVisited');
  if (shopNext && hero0.gold >= 150) { pick = shopNext; why = `shopping with ${hero0.gold} gold`; }
  if (!pick && fights < minFights && fresh.length) {
    let best = null;
    for (const n of reachable.filter(n => n.type !== 'ExitBoss')) {
      let dFresh = 99;
      for (const f of fresh) dFresh = Math.min(dFresh, await distance(n.index, f.index));
      if (!best || dFresh < best.dFresh) best = { n, dFresh };
    }
    if (best) { pick = best.n; why = `${best.dFresh} from a fresh fight (${fights}/${minFights} fights)`; }
  }
  if (!pick) {
    let best = null;
    for (const n of reachable) {
      const dBoss = boss ? await distance(n.index, boss.index) : 99;
      if (!best || dBoss < best.dBoss) best = { n, dBoss };
    }
    pick = best.n; why = `${best.dBoss} from the boss`;
  }
  log(`travel -> node ${pick.index} (${pick.type}, ${pick.status}), ${why}`);
  const exitId = (await get('/state')).room.exitId;
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  await tryPost('/hero/interact', { id: exitId });
  let shown = false;
  for (let i = 0; i < 60 && !shown; i++) {
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    shown = (await get('/map')).worldMapShown === 'Shown';
    if (!shown) await sleep(250);
  }
  if (!shown) { log('the world map did not open at the exit'); return null; }
  const r = await tryPost('/map/travel', { node: pick.index });
  if (r.error || (r.refused && r.refused.length)) { log('travel refused:', r.error || r.refused.join('; ')); return null; }
  for (let i = 0; i < 8; i++) {
    const st = await get('/state');
    if (!acceptLeftovers && st.message && /unclaimed|left behind|on the ground/i.test(st.message.text)) {
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
async function nextZone() {
  const st0 = await get('/state');
  const zone = st0.room.zoneIndex;
  await tryPost('/hero/move_dir', { x: 0, z: 0 });
  await tryPost('/hero/interact', { id: st0.room.exitId });
  const t = Date.now();
  while (Date.now() - t < 30000) {
    const st = await get('/state');
    if (await handleBlocking(st)) continue;
    if (st.room && st.room.zoneIndex !== zone && st.uiState === 'Playing' && !st.loading) {
      await post('/flow/wait_playing', { timeout: 60 });
      return true;
    }
    await sleep(400);
  }
  return false;
}

async function auto(maxRooms = 80, lastZone = 2) {
  for (let room = 0; room < maxRooms; room++) {
    const st = await get('/state');
    log(`=== zone ${st.room && st.room.zoneIndex} ${st.room && st.room.zone}: ${st.room && st.room.room} (node ${st.room && st.room.node}, ${st.room && st.room.nodeType})`);
    let result;
    for (let round = 0; round < 8; round++) {
      result = await fight();
      log('fight:', result);
      if (result === 'dead') return 'dead';
      lootAbort = 12; await loot(); lootAbort = 0;
      if (!(await get('/entities', { kind: 'enemies', radius: 30, limit: 1 })).entities.length) break;
      log('  enemies turned up while looting - back to the fight');
    }
    if (result === 'dead') return 'dead';
    const after = await get('/state');
    if (after.room && after.room.nodeType === 'ExitBoss') {
      const hero = await get('/hero');
      log(`boss of zone ${after.room.zoneIndex} down - hero lvl ${hero.level}, ${Math.round(hero.hp)}/${Math.round(hero.maxHp)} hp`);
      if (after.room.zoneIndex >= lastZone) return `cleared zones 0..${lastZone}`;
      let through = false;
      for (let attempt = 0; attempt < 3 && !through; attempt++) {
        for (let i = 0; i < 20; i++) {
          if ((await get('/interactables', { radius: 100 })).interactables.some(x => x.type === 'Shrine_BossSoul')) break;
          await sleep(500);
        }
        await loot();
        through = await nextZone();
        if (!through) log('  the exit did not take us - looking around again');
      }
      if (!through) { log('could not reach the next zone'); return 'stuck'; }
      fights = 0;
      continue;
    }
    for (let i = 0; i < 20 && !(await get('/state')).room.exitOpen; i++) await sleep(500);
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
  for (let i = 0; i < 30; i++) {
    try { log('done:', await run()); return; }
    catch (e) { log('error:', e.message, '- carrying on'); await tryPost('/hero/move_dir', { x: 0, z: 0 }); await sleep(500); }
  }
})();
