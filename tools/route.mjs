// Choosing the next node on the world map while the bot still wants fights. Pure: the caller hands
// in the node indices and a distance function (ZoneManager.GetNodeDistance, read ahead and cached),
// so it can be tested offline (tests/route.test.mjs).
//
// Before, the bot went to whichever reachable node was nearest a fresh combat node, and to the
// boss only once it had its fights. Nearest-first wanders: in run 28 the sixth fight left the hero
// 3 rooms from the boss (zone 0) and 2 rooms (zone 1), in run 24 2 rooms - each a room of ~35 s.
// Here each reachable node is scored by the shortest walk that starts there, takes in `need`
// fresh combat nodes and ends at the boss; the shortest wins. Fewer rooms, the same fights.
//
//   reachable  indices the hero can travel to now (the boss excluded by the caller while it wants fights)
//   fresh      indices of combat nodes not yet visited
//   boss       index of the boss node (null: no boss known - nearest fresh fight wins)
//   need       fights still wanted (>= 1)
//   D(a, b)    rooms between two nodes (0 for the same node)
//
// Returns { node, cost, dFresh } - cost is the rooms from `node` to the boss on the best walk.
export function chooseFightNode({ reachable, fresh, boss, need, D }) {
  let best = null;
  for (const n of reachable) {
    const isFresh = fresh.includes(n);
    const left = fresh.filter(f => f !== n);
    const k = Math.max(0, need - (isFresh ? 1 : 0));
    const cost = boss == null ? 0 : tail(n, nearest(n, left, D, 10), Math.min(k, left.length), boss, D, Infinity);
    const dFresh = isFresh ? 0 : Math.min(99, ...left.map(f => D(n, f)));
    const dBoss = boss == null ? 0 : D(n, boss);
    // Ties: the fight that comes sooner, then the node nearer the boss, then the lower index.
    const key = [cost, dFresh, dBoss, n];
    if (!best || less(key, best.key)) best = { node: n, cost, dFresh, key };
  }
  return best && { node: best.node, cost: best.cost, dFresh: best.dFresh };
}

const less = (a, b) => { for (let i = 0; i < a.length; i++) if (a[i] !== b[i]) return a[i] < b[i]; return false; };

// The `max` fresh nodes nearest `from` - enough to choose from, and it keeps the search small.
function nearest(from, list, D, max) {
  return list.slice().sort((a, b) => D(from, a) - D(from, b) || a - b).slice(0, max);
}

// Fewest rooms from `pos` through `k` of `left` (any order) to `boss`. Exhaustive with pruning:
// at most 10 candidates and k <= 6 fights, a few thousand steps.
function tail(pos, left, k, boss, D, bound) {
  if (k <= 0 || !left.length) return D(pos, boss);
  let best = bound;
  for (let i = 0; i < left.length; i++) {
    const d = D(pos, left[i]);
    if (d >= best) continue;
    const rest = left.slice(0, i).concat(left.slice(i + 1));
    const c = d + tail(left[i], rest, k - 1, boss, D, best - d);
    if (c < best) best = c;
  }
  return best;
}

// Whether the fights still wanted are worth the way to them. chooseFightNode takes `need` fights
// whatever the walk: in run-004's zone 0 the hero had 5 of 6 at node 1, the boss 2 rooms off, and
// the nearest fresh fight 3 rooms the other way - it went 4 -> 6 -> 9 (one Spider Warrior, 5 s)
// -> 6 -> 4 -> boss: 4 rooms and ~50 s more than 4 -> boss. A fight is worth about one room of
// walking - the room it is in - so k fights are taken only when their walk is at most k rooms
// longer than the straight way; else fewer (k = need .. 1), else none (go to the boss).
//   direct  rooms from the nearest reachable node to the boss (0: the boss is reachable)
//   perRoom fights a room off the straight way has to bring (iter-5: 1). iter-8 makes it 2: a room
//           is ~35-45 s of the zone's clock, the fights on the straight way come free, and the zone
//           bosses of run-007/008 went down at full health (the Seeker after 3 fights of 6).
//           run-008 planned 4, 2 and 3 rooms over the straight way at the start of zones 0-2.
// Returns chooseFightNode's { node, cost, dFresh } plus took (fights planned) and extra (rooms more
// than direct), or { node: null, extra } (extra for a single fight) - go straight to the boss.
export function chooseRoute({ reachable, fresh, boss, need, D, direct, perRoom = 1 }) {
  const first = chooseFightNode({ reachable, fresh, boss, need, D });
  if (!first || boss == null) return first && { ...first, took: need, extra: 0 };
  for (let k = need; k >= 1; k--) {
    const c = k === need ? first : chooseFightNode({ reachable, fresh, boss, need: k, D });
    const extra = c.cost - direct;
    if (extra * perRoom <= k) return { ...c, took: k, extra };
    if (k === 1) return { node: null, extra };
  }
  return { node: null, extra: first.cost - direct };
}

// Iteration 38: what a merchant node costs the route. travel() took a merchant whenever it was nearer the boss than the
// current node, as if the room were free - but a merchant room holds no fight, so while fights are still wanted the fight it
// stands in for has to be found off the way: after 5 of the 10 shopping trips in runs 030-047 the route said "1 rooms over
// the straight way" (~40 s a combat room). Compared here: the rooms from the next node to the boss through the shop (taking
// the same fights the best route takes) against the best route (chooseRoute) - extra = the rooms the shop adds.
//   reachable, fresh, boss, need, D, direct, perRoom as chooseRoute; shop the merchant node's index (one of reachable).
// Returns { extra, via, best, took }.
export function shopDetour({ reachable, shop, fresh, boss, need, D, direct, perRoom = 2 }) {
  if (boss == null) return { extra: 0, via: null, best: null, took: 0 };
  let best = direct, took = 0;
  if (need > 0 && fresh.length) {
    const c = chooseRoute({ reachable, fresh, boss, need, D, direct, perRoom });
    if (c && c.node !== null) { best = c.cost; took = c.took; }
  }
  const via = took > 0 ? chooseFightNode({ reachable: [shop], fresh, boss, need: took, D }).cost : D(shop, boss);
  return { extra: via - best, via, best, took };
}
