#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace DevTools
{
    // What is flying at the hero: enemy projectiles in the air, each with where it is headed and
    // how close it will pass. A player reads the same from the screen - the bolt, its direction,
    // whether it curves after them - and steps aside; this is that, as numbers.
    //
    //   heading   the way it is going now, flattened: toward its target entity when it homes,
    //             otherwise toward the point it flies to (for a straight shot, its range's end)
    //   miss      how close it passes the hero's current position on that line (0 = straight at)
    //   eta       seconds until it is there, at its speed
    //   homing    it follows the hero; stepping aside only works at the last moment
    //
    // And the red shapes on the ground that fill up before a blow lands (telegraphs), read from
    // the drawing itself, so what is listed is what a player sees:
    //
    //   circle    centre, radius (and an inner radius for a ring); a slice also has its angle
    //             and the way it faces
    //   box       its four corners on the ground
    //   fill      how far it has filled, 0..1: the blow lands when it is full
    //   left      seconds until then
    //   edge      how far the hero has to go to get out of it (0 when outside)
    //   strike    a blow that lands at a point after a delay, read from the damage itself: its
    //             real radius and the seconds until it lands (type names it); a blob at the same
    //             spot is its drawing
    //   zone      ground that hurts while stood in (burning pools, the Ink boss room's ground)
    //   pull      Nyx's Blackhole: where it hurts, and how far it pulls
    //   safe      the one shape that is not red: where White Night's Cataclysm does not reach
    internal static class Threats
    {
        [Route("GET", "/threats", "What is about to hit: enemy projectiles in the air (position, heading, speed, collision radius, homing, how close it will pass - miss - and when - eta) and the red telegraphs on the ground (circles, slices, boxes; how full, seconds left, whether the hero stands in one). For dodging.",
               "radius=25")]
        private static object List(Args a)
        {
            var hero = GameAccess.RequireHero();
            float radius = a.Float("radius", 25f);
            var h = hero.agentPosition;

            var projectiles = Describe.Actors<Projectile>()
                .Where(p => p != null && p.isActive && !p.isCompleted && Hostile(hero, p))
                .Where(p => Flat(p.position - h).magnitude <= radius)
                .Select(p => Read(p, hero, h))
                .Where(x => x != null)
                .OrderBy(x => x.eta)
                .ToList();

            var areas = Telegraphs(hero, h, radius);
            // readers: which readers this build has (39: Primus's polygon blows, its Jump Attack's cones and its Rage swipes;
            // 41: the Displacers' dashes - `dashers`, the wind-up's box typed, the dash itself as a box - and a miniboss's
            // spinning arrows (Se_MiniBoss_SpinningArrow) - `spinners`; 42: Primus's Adapt phase - its Adapt Atk from the wind-up
            // and in flight with the chain's next links, the Arbalest's aim line, Rage's Atk and Dash Attack from their wind-up,
            // and `primus`: its phase and weapon; 46: `doom` - Primus's Doom meteors and the spokes their fireball rings fly along;
            // 48: Despair's Dread Bug and Unstable Rat dash attacks under way, boxes typed Ai_Mon_Despair_DreadBug_Atk / _UnstableRat_Atk;
            // 49: White Night's Destruction Wave as its real polygon - the spike now, the fan it sweeps, the spike at the turn's end;
            // 50: Azurak's Atk from its wind-up where the blow lands - ahead of him, not on him;
            // 51: Dark Moon's Blade as its real polygon (a crescent; timed by her channel) and her ShortDash's lance, polygons of
            // Dark Moon and Infernus listed (Infernus's Atk from its wind-up);
            // 52: Belphomet's Atk, a dash at the hero, as a box from its wind-up and while it dashes;
            // 54: monsters' blows from their wind-up - the Snow Wolf's Pounce (its lane), Big Baam's beam (its lanes), the Soul
            // Swordsman's SwiftStep (a strike where it will land, then its slash's real polygon and its shots' lines) - the
            // Pounce under way, the Seeker's polygons (its Claw) and Dark Moon's hallucinations' Blade_RageInstance).
            return new { hero = Describe.Vec(h), projectiles, areas, inside = areas.Any(x => x.inside && x.shape != "safe"), readers = 54,
                         dashers = Dashers(hero, h, radius), spinners = Spinners(hero, h, radius), primus = PrimusState(hero),
                         doom = DoomMeteors(hero, h, radius) };
        }

        private static bool Hostile(Hero hero, Projectile p)
        {
            try { return Hostile(hero, p.info.caster); }
            catch (Exception) { return false; }
        }

        private static bool Hostile(Hero hero, Entity caster)
        {
            try { return caster != null && hero.GetRelation(caster) == EntityRelation.Enemy; }
            catch (Exception) { return false; }
        }

        // ----- telegraphs -------------------------------------------------------------------

        private sealed class Area
        {
            public string shape;
            public string by;
            public object centre;
            public float radius;
            public float inner;
            public float angle;
            public object facing;
            public object[] corners;
            public float fill;
            public float left;
            public bool inside;
            public float edge;
            public string type;
            public float pull;   // how far it pulls the hero toward its centre (Nyx's Blackhole); 0 if it does not
        }

        private static List<Area> Telegraphs(Hero hero, Vector3 h, float radius)
        {
            var list = new List<Area>();
            foreach (var arc in UnityEngine.Object.FindObjectsByType<ArcTelegraphController>(FindObjectsSortMode.None))
            {
                if (arc == null || !arc.isActiveAndEnabled || !arc.isPlaying || arc.whiteBorder == null) continue;
                var caster = CasterOf(arc);
                if (caster != null && !Hostile(hero, caster)) continue;
                // The drawing is a quad scaled to the outer diameter.
                var t = arc.whiteBorder.transform;
                var c = t.position;
                float outer = t.lossyScale.x * 0.5f;
                if (outer <= 0.01f || Flat(c - h).magnitude > radius + outer) continue;
                float inner = arc.outerRadius > 0.001f ? outer * arc.innerRadius / arc.outerRadius : 0f;
                bool full = arc.arcAngle >= 359f;
                var fwd = Flat(arc.transform.forward).normalized;

                var rel = Flat(h - c);
                float d = rel.magnitude;
                bool inAngle = full || d < 0.01f || Vector3.Angle(fwd, rel) <= arc.arcAngle * 0.5f;
                bool inside = d <= outer && d >= inner && inAngle;
                float edge = !inside ? 0f : inner > 0.05f ? Mathf.Min(outer - d, d - inner) : outer - d;

                list.Add(new Area
                {
                    shape = full ? (inner > 0.05f ? "ring" : "circle") : "slice",
                    by = caster != null ? Describe.EntityName(caster) : null,
                    centre = Describe.Vec(c),
                    radius = R(outer),
                    inner = R(inner),
                    angle = R(arc.arcAngle),
                    facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                    fill = R(arc.value),
                    left = R(Mathf.Max(0f, (1f - arc.value) * arc.duration)),
                    inside = inside,
                    edge = R(edge),
                });
            }

            foreach (var box in UnityEngine.Object.FindObjectsByType<BoxTelegraphController>(FindObjectsSortMode.None))
            {
                if (box == null || !box.isActiveAndEnabled || !box.isPlaying) continue;
                var caster = CasterOf(box);
                if (caster != null && !Hostile(hero, caster)) continue;
                // Iteration 41: a Displacer's dash lane drawn while it winds the dash up is typed (DashWindupType).
                string boxType = DashWindupType(caster);
                // The corners as the component's own gizmo draws them, in its local space.
                var t = box.transform;
                float k = 1f / Mathf.Max(0.0001f, t.localScale.x);
                float hx = box.width * 0.5f * k, hz = box.height * 0.5f * k;
                var world = new[]
                {
                    t.TransformPoint(new Vector3(-hx, 0f, hz)),
                    t.TransformPoint(new Vector3(hx, 0f, hz)),
                    t.TransformPoint(new Vector3(hx, 0f, -hz)),
                    t.TransformPoint(new Vector3(-hx, 0f, -hz)),
                };
                var centre = (world[0] + world[2]) * 0.5f;
                if (Flat(centre - h).magnitude > radius + box.height) continue;

                var p = t.InverseTransformPoint(h);
                bool inside = Mathf.Abs(p.x) <= hx && Mathf.Abs(p.z) <= hz;
                float edge = inside ? Mathf.Min(hx - Mathf.Abs(p.x), hz - Mathf.Abs(p.z)) * t.lossyScale.x : 0f;
                var fwd = Flat(t.forward).normalized;

                list.Add(new Area
                {
                    shape = "box",
                    by = caster != null ? Describe.EntityName(caster) : null,
                    centre = Describe.Vec(centre),
                    corners = world.Select(v => Describe.Vec(v)).ToArray(),
                    facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                    fill = R(box.value),
                    left = R(Mathf.Max(0f, (1f - box.value) * box.duration)),
                    inside = inside,
                    edge = R(edge),
                    type = boxType,
                });
            }
            // Telegraphs drawn some other way (particles, decals): a circle around what is drawn.
            // No fill or time for these - only that one is on the ground and where.
            foreach (var fx in UnityEngine.Object.FindObjectsByType<FxCastTelegraph>(FindObjectsSortMode.None))
            {
                if (fx == null || !fx.isActiveAndEnabled) continue;
                if (fx.GetComponentsInChildren<ArcTelegraphController>().Any(x => x.isPlaying) ||
                    fx.GetComponentsInChildren<BoxTelegraphController>().Any(x => x.isPlaying)) continue;
                Entity caster = null;
                try { caster = fx.castInfo.caster; } catch (Exception) { }
                if (caster != null && !Hostile(hero, caster)) continue;
                var renderers = fx.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.gameObject.activeInHierarchy).ToArray();
                if (renderers.Length == 0) continue;
                var ps = fx.GetComponentsInChildren<ParticleSystem>();
                if (ps.Length > 0 && !ps.Any(p => p.isPlaying || p.particleCount > 0)) continue;
                var bounds = renderers[0].bounds;
                foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                var c = bounds.center;
                float rad = Mathf.Max(bounds.extents.x, bounds.extents.z);
                if (rad < 0.2f || rad > 40f || Flat(c - h).magnitude > radius + rad) continue;
                float d = Flat(h - c).magnitude;
                list.Add(new Area
                {
                    shape = "blob",
                    by = caster != null ? Describe.EntityName(caster) : null,
                    centre = Describe.Vec(c),
                    radius = R(rad),
                    angle = 360f,
                    fill = -1f,
                    left = -1f,
                    inside = d <= rad,
                    edge = R(Mathf.Max(0f, rad - d)),
                });
            }

            // Blows that land at a point after a delay (Skoll's swords, the Seeker's delayed
            // explosions, Starfall): the damage's own collider and the time until it goes off. The
            // blob read above for the same blow is only the bounds of its drawing, which is larger.
            foreach (var di in Describe.Actors<InstantDamageInstance>())
            {
                try
                {
                    if (di == null || !di.isActive || di.range == null) continue;
                    var caster = di.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float delay = di.damageDelay;
                    if (di.affectedByAttackSpeed && caster != null && !caster.IsNullInactiveDeadOrKnockedOut())
                        delay /= Mathf.Max(0.01f, caster.Status.attackSpeedMultiplier);
                    float left = delay - (Time.time - di.creationTime);
                    if (left < 0f) continue;
                    var col = di.range;
                    // Primus's sword swings and its Jump Attack's cones have a polygon collider: their outline, a "poly".
                    if (col.shape == DewCollider.ColliderShape.Polygon)
                    {
                        if (!PolyCaster(caster)) continue;
                        var poly = PolyNow(di, col);
                        if (poly != null)
                            AddPoly(list, h, radius, poly, Flat(di.transform.forward).normalized, left,
                                delay > 0.01f ? Mathf.Clamp01(1f - left / delay) : 1f, caster, di.GetType().Name);
                        continue;
                    }
                    var t = col.transform;
                    var c = t.position + t.rotation * new Vector3(col.offset.x, 0f, col.offset.y);
                    float rad;
                    if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * t.lossyScale.x;
                    else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * t.lossyScale.x, col.size.y * t.lossyScale.z);
                    else continue;
                    if (rad <= 0.05f || Flat(c - h).magnitude > radius + rad) continue;
                    float d = Flat(h - c).magnitude;
                    list.Add(new Area
                    {
                        shape = col.shape == DewCollider.ColliderShape.Circle ? "strike" : "strikebox",
                        by = caster != null ? Describe.EntityName(caster) : null,
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = R(delay > 0.01f ? Mathf.Clamp01(1f - left / delay) : 1f),
                        left = R(left),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = di.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Big Baam's beam: a line from the caster along its cast direction whose end grows
            // outward over the channel. A box from the start to its full reach; left = seconds until
            // the growing end reaches the hero's distance along it, fill = how far through the channel.
            foreach (var bm in Describe.Actors<Ai_Mon_Sky_BigBaam_BeamAtk>())
            {
                try
                {
                    if (bm == null || !bm.isActive) continue;
                    var caster = bm.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    float dur = Mathf.Max(0.01f, bm.Network_beamDuration);
                    float age = Time.time - bm.creationTime;
                    var o = caster.position + caster.rotation * bm.startOffset;
                    var fwd = Flat(bm.info.forward).normalized;
                    float reach = bm.distanceCurve.Evaluate(1f);
                    float along = Vector3.Dot(Flat(h - o), fwd);
                    float left = -1f;
                    for (int i = 0; i <= 20; i++)
                    {
                        float k = i / 20f;
                        if (bm.distanceCurve.Evaluate(k) >= along) { left = Mathf.Max(0f, k * dur - age); break; }
                    }
                    if (left < 0f || Flat(o - h).magnitude > radius + reach) continue;
                    var across = new Vector3(fwd.z, 0f, -fwd.x);
                    var side = across * bm.beamRadius;
                    var end = o + fwd * reach;
                    var world = new[] { o - side, o + side, end + side, end - side };
                    float off = Mathf.Abs(Vector3.Dot(Flat(h - o), across));
                    bool inside = along >= 0f && along <= reach && off <= bm.beamRadius;
                    list.Add(new Area
                    {
                        shape = "box",
                        by = Describe.EntityName(caster),
                        centre = Describe.Vec((o + end) * 0.5f),
                        corners = world.Select(v => Describe.Vec(v)).ToArray(),
                        facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                        fill = R(Mathf.Clamp01(age / dur)),
                        left = R(left),
                        inside = inside,
                        edge = R(inside ? bm.beamRadius - off : 0f),
                        type = bm.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Big Baam's root: a strike at its point after explodeDelay (an ability, not a damage
            // instance, so the strikes above miss it).
            foreach (var rt in Describe.Actors<Ai_Mon_Sky_BigBaam_Main_Root>())
            {
                try
                {
                    if (rt == null || !rt.isActive || rt.range == null) continue;
                    var caster = rt.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float left = rt.explodeDelay - (Time.time - rt.creationTime);
                    if (left < 0f) continue;
                    var col = rt.range;
                    if (col.shape != DewCollider.ColliderShape.Circle) continue;
                    var t = col.transform;
                    var c = t.position + t.rotation * new Vector3(col.offset.x, 0f, col.offset.y);
                    float rad = col.radius * t.lossyScale.x;
                    float d = Flat(h - c).magnitude;
                    if (rad <= 0.05f || d > radius + rad) continue;
                    list.Add(new Area
                    {
                        shape = "strike",
                        by = caster != null ? Describe.EntityName(caster) : null,
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = R(rt.explodeDelay > 0.01f ? Mathf.Clamp01(1f - left / rt.explodeDelay) : 1f),
                        left = R(left),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = rt.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Burning ground (a Fire Elemental's explosion or self-destruct, Magmadon's charge):
            // hurts whoever stands in it for its whole life, its caster dead or not - so only a
            // caster known to be friendly hides it. left = seconds until it goes out.
            foreach (var z in Describe.Actors<Ai_Mon_LavaLand_FireElemental_ExplosionSub>())
            {
                try
                {
                    if (z == null || !z.isActive || z.range == null) continue;
                    var caster = z.info.caster;
                    bool friendly = false;
                    try { friendly = caster != null && hero.GetRelation(caster) != EntityRelation.Enemy; } catch (Exception) { }
                    if (friendly) continue;
                    float left = z.existTime - (Time.time - z.creationTime);
                    if (left < 0f) continue;
                    var col = z.range;
                    var t = col.transform;
                    var c = t.position + t.rotation * new Vector3(col.offset.x, 0f, col.offset.y);
                    float rad;
                    if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * t.lossyScale.x;
                    else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * t.lossyScale.x, col.size.y * t.lossyScale.z);
                    else continue;
                    float d = Flat(h - c).magnitude;
                    if (rad <= 0.05f || d > radius + rad) continue;
                    list.Add(new Area
                    {
                        shape = "zone",
                        by = caster != null ? Describe.EntityName(caster) : null,
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = 1f,
                        left = R(left),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = z.GetType().Name,
                    });
                }
                catch (Exception) { }
            }
            // Nyx's Blackhole: she flies to the room's centre, then pulls everyone in and hurts
            // whoever is within tickDamageRadius, more every tick. shape "pull": centre, radius =
            // where it hurts, pull = how far it pulls; fill 0 while she flies in (left = seconds until
            // it opens), 1 while it is open (left = seconds until it closes).
            foreach (var bh in Describe.Actors<Ai_Mon_Sky_BossNyx_Blackhole>())
            {
                try
                {
                    if (bh == null || !bh.isActive) continue;
                    var caster = bh.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    bool on = bh.Network_isBlackholeOn;
                    float started = bh.Network_blackholeStartNetworkTime;
                    if (!on && started > 0f) continue;   // over (its end blow is instant)
                    var roomCentre = SingletonBehaviour<Sky_BossRoomCenter>.instance;
                    var c = on || roomCentre == null ? caster.position : roomCentre.transform.position;
                    float left = on
                        ? bh.blackholeDuration - (float)(NetworkTime.time - started)
                        : bh.displaceDuration - (Time.time - bh.creationTime);
                    float rad = bh.tickDamageRadius;
                    float d = Flat(h - c).magnitude;
                    if (d > radius + Mathf.Max(rad, bh.distanceBounds.y)) continue;
                    list.Add(new Area
                    {
                        shape = "pull",
                        by = Describe.EntityName(caster),
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = on ? 1f : 0f,
                        left = R(Mathf.Max(0f, left)),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        pull = R(bh.distanceBounds.y),
                        type = bh.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Nyx's Pillar of Stars: a telegraph for initDelay, then ticks on whoever is in its range
            // for tickCount x tickInterval. Before it starts a "strike" (left = seconds until the
            // first tick); while it ticks a "zone" (left = seconds until the last).
            foreach (var ps in Describe.Actors<Ai_Mon_Sky_BossNyx_PillarOfStars>())
            {
                try
                {
                    if (ps == null || !ps.isActive || ps.range == null) continue;
                    var caster = ps.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float age = Time.time - ps.creationTime;
                    float ticking = ps.tickCount * ps.tickInterval;
                    bool before = age < ps.initDelay;
                    float left = before ? ps.initDelay - age : ps.initDelay + ticking - age;
                    if (left < 0f) continue;
                    var col = ps.range;
                    var t = col.transform;
                    var c = t.position + t.rotation * new Vector3(col.offset.x, 0f, col.offset.y);
                    float rad;
                    if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * t.lossyScale.x;
                    else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * t.lossyScale.x, col.size.y * t.lossyScale.z);
                    else continue;
                    float d = Flat(h - c).magnitude;
                    if (rad <= 0.05f || d > radius + rad) continue;
                    list.Add(new Area
                    {
                        shape = before ? "strike" : "zone",
                        by = caster != null ? Describe.EntityName(caster) : null,
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = before ? R(ps.initDelay > 0.01f ? Mathf.Clamp01(age / ps.initDelay) : 1f) : 1f,
                        left = R(left),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = ps.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // White Night's Cataclysm (zone 2, Ink): the safe circles of the wave going on. shape
            // "safe": NOT red - the one place its blow does not reach. centre, radius, left = seconds
            // until the safe zone stops checking (the blow lands about then); inside = the hero is in it.
            foreach (var sz in Describe.Actors<Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone>())
            {
                try
                {
                    if (sz == null || !sz.isActive) continue;
                    var caster = sz.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    var points = SafePoints(sz);
                    if (points == null) continue;
                    float left = SafeEnd(sz) - Time.time;
                    if (left < -0.3f) continue;
                    float rad = sz.Network_radius;
                    foreach (var p in points)
                    {
                        float d = Flat(h - p).magnitude;
                        if (d > radius + rad + 30f) continue;   // the far circles matter too: the nearest may be off the path
                        list.Add(new Area
                        {
                            shape = "safe",
                            by = caster != null ? Describe.EntityName(caster) : null,
                            centre = Describe.Vec(p),
                            radius = R(rad),
                            angle = 360f,
                            fill = 1f,
                            left = R(Mathf.Max(0f, left)),
                            inside = d <= rad,
                            edge = R(Mathf.Max(0f, rad - d)),
                            type = sz.GetType().Name,
                        });
                    }
                }
                catch (Exception) { }
            }

            // The Ink boss room's damaging ground: a "zone" while it is on (left 99: until the room clears).
            foreach (var g in Describe.Actors<Ink_BossRoomDamageGround>())
            {
                try
                {
                    if (g == null || !GroundOn(g)) continue;
                    var c = g.transform.position;
                    float d = Flat(h - c).magnitude;
                    if (g.radius <= 0.05f || d > radius + g.radius) continue;
                    list.Add(new Area
                    {
                        shape = "zone",
                        centre = Describe.Vec(c),
                        radius = R(g.radius),
                        angle = 360f,
                        fill = 1f,
                        left = 99f,
                        inside = d <= g.radius,
                        edge = R(Mathf.Max(0f, g.radius - d)),
                        type = g.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Dark Moon's Blade (zone 2, Ink): she dashes to the hero, then channels, and the blow
            // lands where she stands, facing where she turned. Listed from the end of her dash until
            // the blow: the blow's own range (the prefab's) at her position and facing.
            var liveBlades = new HashSet<uint>();
            foreach (var bl in Describe.Actors<Ai_Mon_Ink_BossDarkMoon_Blade>())
            {
                try
                {
                    if (bl == null || !bl.isActive) continue;
                    var caster = bl.info.caster;
                    if (caster == null || !Hostile(hero, caster) || caster.Control.isDisplacing) continue;
                    liveBlades.Add(bl.netId);
                    if (!BladeStart.TryGetValue(bl.netId, out var began)) { began = Time.time; BladeStart[bl.netId] = began; }
                    // In rage atkPrepareDuration has already been set to the rage length.
                    float dur = bl.atkPrepareDuration / Mathf.Max(0.1f, caster.Status.attackSpeedMultiplier);
                    float left = dur - (Time.time - began);
                    // Iteration 51: the time left from her channel itself (the Blade's: StartChannel when the dash ends), not
                    // from the first look after it.
                    Channel bch = null;
                    foreach (var c in caster.Control.ongoingChannels)
                        if (c != null && c.isAlive && c.duration > 0.05f && (bch == null || c.duration - c.elapsedTime > bch.duration - bch.elapsedTime)) bch = c;
                    if (bch != null && bch.duration < 3f) { dur = bch.duration; left = bch.duration - bch.elapsedTime; }
                    if (left < -0.1f) continue;
                    var inst = DewResources.GetByType<Ai_Mon_Ink_BossDarkMoon_Blade_Instance>();
                    if (inst == null || inst.range == null) continue;
                    float bfill = dur > 0.01f ? Mathf.Clamp01(1f - left / dur) : 1f;
                    // Iteration 51: its range is a polygon (21 points: a crescent round her front and sides, 2.3 -> 6.1-6.8 m;
                    // read out of the game's bundle), which AddColliderStrike listed for Primus only - so the Blade was never
                    // listed (runs 038-054: 187-442 in every Dark Moon fight). Listed as a "poly" at her place, turned as she is.
                    if (inst.range.shape == DewCollider.ColliderShape.Polygon)
                    {
                        var bpoly = PolyAt(inst.range, inst.transform, caster.position, caster.rotation);
                        if (bpoly != null) AddPoly(list, h, radius, bpoly, Flat(caster.transform.forward).normalized, Mathf.Max(0f, left), bfill, caster, bl.GetType().Name);
                        continue;
                    }
                    AddColliderStrike(list, h, radius, inst.range, caster.position, caster.rotation, Mathf.Max(0f, left),
                        bfill, caster, bl.GetType().Name);
                }
                catch (Exception) { }
            }
            foreach (var id in BladeStart.Keys.Where(k => !liveBlades.Contains(k)).ToList()) BladeStart.Remove(id);

            // Dark Moon's ShortDash in rage (or alone): she stands turning toward the hero, then
            // dashes dashDistance along her facing, hitting everyone on the way. A box along that line.
            foreach (var sd in Describe.Actors<Ai_Mon_Ink_BossDarkMoon_ShortDash>())
            {
                try
                {
                    if (sd == null || !sd.isActive) continue;
                    var caster = sd.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    if (!(caster is Mon_Ink_BossDarkMoon dm) || !(dm.Network_isRage || dm.Network_isSolo)) continue;
                    float total = sd.startDelay + sd.castDuration;   // castDuration is already x0.75 in rage
                    float left = total - (Time.time - sd.creationTime);
                    if (left < -0.1f) continue;
                    var atk = DewResources.GetByType<Ai_Mon_Ink_BossDarkMoon_ShortDash_Atk>();
                    if (atk == null || atk.range == null) continue;
                    var fwd = Flat(caster.transform.forward).normalized;
                    var o = caster.position;
                    // Iteration 51: the Atk's range is a polygon too (a lance from 0.5 m behind her to 10.37 m ahead, 1.8 m
                    // wide, placed where the dash starts - it hurts once, 0.05 s in), which the box below read as 3 m wide and
                    // 8.5 m long (RangeHalfWidth's 1.5 for a polygon): listed as its "poly" at her place, turned as she faces.
                    if (atk.range.shape == DewCollider.ColliderShape.Polygon)
                    {
                        var lance = PolyAt(atk.range, atk.transform, o, Quaternion.LookRotation(fwd));
                        if (lance != null) AddPoly(list, h, radius, lance, fwd, Mathf.Max(0f, left), total > 0.01f ? Mathf.Clamp01(1f - left / total) : 1f, caster, sd.GetType().Name);
                        continue;
                    }
                    var end = o + fwd * atk.dashDistance;
                    float half = RangeHalfWidth(atk.range);
                    var across = new Vector3(fwd.z, 0f, -fwd.x);
                    var world = new[] { o - across * half, o + across * half, end + across * half, end - across * half };
                    var rel = Flat(h - o);
                    float along = Vector3.Dot(rel, fwd), off = Mathf.Abs(Vector3.Dot(rel, across));
                    bool inside = along >= -half && along <= atk.dashDistance + half && off <= half;
                    if (Flat(o - h).magnitude > radius + atk.dashDistance) continue;
                    list.Add(new Area
                    {
                        shape = "box",
                        by = Describe.EntityName(caster),
                        centre = Describe.Vec((o + end) * 0.5f),
                        corners = world.Select(v => Describe.Vec(v)).ToArray(),
                        facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                        fill = R(total > 0.01f ? Mathf.Clamp01(1f - left / total) : 1f),
                        left = R(Mathf.Max(0f, left)),
                        inside = inside,
                        edge = R(inside ? half - off : 0f),
                        type = sd.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // Skoll's Death From Above: once the follow starts (_followPos set), he lands where it is when
            // followDuration + descendTime have passed - the Land's own range there, as a strike.
            var liveFalls = new HashSet<uint>();
            foreach (var fa in Describe.Actors<Ai_Mon_SnowMountain_BossSkoll_DeathFromAbove>())
            {
                try
                {
                    if (fa == null || !fa.isActive) continue;
                    var caster = fa.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    var at = fa.Network_followPos;
                    if (at == Vector3.zero) continue;   // not following yet (the sword waves)
                    liveFalls.Add(fa.netId);
                    if (!FallStart.TryGetValue(fa.netId, out var began)) { began = Time.time; FallStart[fa.netId] = began; }
                    float total = fa.followDuration + fa.descendTime;
                    float left = total - (Time.time - began);
                    if (left < -0.1f) continue;
                    var land = DewResources.GetByType<Ai_Mon_SnowMountain_BossSkoll_DeathFromAbove_Land>();
                    if (land == null || land.range == null) continue;
                    AddColliderStrike(list, h, radius, land.range, at, Quaternion.identity, Mathf.Max(0f, left),
                        total > 0.01f ? Mathf.Clamp01(1f - left / total) : 1f, caster, "Ai_Mon_SnowMountain_BossSkoll_DeathFromAbove_Landing");
                }
                catch (Exception) { }
            }
            foreach (var id in FallStart.Keys.Where(k => !liveFalls.Contains(k)).ToList()) FallStart.Remove(id);

            // A zone boss's blow under way: while one of its triggers is casting (Network_isCasting) and the
            // channel runs, the instance that trigger spawns at the end - an InstantDamageInstance, or White
            // Night's Buddha's Palm - is listed as a strike: its own collider at the boss's position and
            // facing (the default spawn place), landing when the channel ends.
            var liveJumps = new HashSet<uint>();
            foreach (var boss in Describe.Actors<Monster>())
            {
                try
                {
                    if (boss == null || !boss.isActive || !boss.isAlive || boss.type.ToString() != "Boss" || !Hostile(hero, boss)) continue;
                    if (Flat(boss.position - h).magnitude > radius + 12f) continue;
                    Channel ch = null;
                    foreach (var c in boss.Control.ongoingChannels)
                        if (c != null && c.isAlive && c.duration > 0.05f && (ch == null || c.duration - c.elapsedTime > ch.duration - ch.elapsedTime)) ch = c;
                    if (ch == null) continue;
                    foreach (var t in boss.Ability.abilities.Values)
                    {
                        if (t == null || !t.Network_isCasting) continue;
                        var inst = t.currentConfig.spawnedInstance;
                        // Primus's Jump Attack: it leaps (dashDuration) to the point it was cast at - the hero's place as the cast
                        // began - and lands a circle and five cones there that turn 90 deg and hit ~0.7 s later. The cones' start
                        // angle is random, so until it lands their whole reach round that point, as a strike.
                        if (inst is Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack ja)
                        {
                            liveJumps.Add(t.netId);
                            if (!JumpCast.TryGetValue(t.netId, out var at)) { at = h; JumpCast[t.netId] = at; }
                            float jl = ch.duration - ch.elapsedTime + ja.dashDuration + ConeDelay(boss);
                            JumpDisc(list, h, radius, at, Mathf.Max(0f, jl),
                                ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / (ch.duration + ja.dashDuration)) : 0f, boss, "Windup_" + t.GetType().Name);
                            continue;
                        }
                        // Iteration 42: Primus's Adapt phase attack (its basic attack there) - a bolt shot at the point it is cast at
                        // (the hero's place; the drawing is a blob of 2.36 x the blow's radius there), landing after the channel and
                        // a short flight: a strike of groundHitChainRadius at that point, and the chain's next links predicted.
                        if (inst is Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk aa)
                        {
                            liveJumps.Add(t.netId);
                            AdaptAtkWindup(list, h, radius, boss, t, aa, Mathf.Max(0f, ch.duration - ch.elapsedTime),
                                ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) : 1f);
                            continue;
                        }
                        // Iteration 42: Rage's Atk trigger spawns the Rage_Atk (two channels, two swipes) where Primus stands, facing
                        // where it turns: both swipes from the trigger's own wind-up (the Rage_Atk reader below takes over once it exists).
                        if (inst is Ai_Mon_Primus_BossPrimusAeron_Rage_Atk rp)
                        {
                            RageSwipes(list, h, radius, boss, boss.position, boss.Control.desiredRotation, rp, Mathf.Max(0f, ch.duration - ch.elapsedTime),
                                ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) * 0.5f : 0f, "Windup_" + t.GetType().Name);
                            continue;
                        }
                        // Iteration 42: Rage's Dash Attack - Primus dashes dash.distance along where it faces with the blow on it: a box
                        // along the dash (the blow's own collider is listed below as well).
                        if (inst is Ai_Mon_Primus_BossPrimusAeron_Rage_DashAttack rd && rd.dash != null && rd.range != null)
                        {
                            float half = RangeHalfWidth(rd.range);
                            AddLine(list, h, radius, boss.position, Flat(boss.Control.desiredRotation * Vector3.forward).normalized, rd.dash.distance + half, half,
                                Mathf.Max(0f, ch.duration - ch.elapsedTime), 0f, ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) : 1f, boss,
                                "Windup_" + t.GetType().Name + " (dash)");
                        }
                        // Iteration 52: Belphomet's Atk (Ai_Mon_Forest_BossDemon_Atk, a DashAttackInstance): as the channel ends it dashes
                        // dash.distance toward the point it is cast at (the hero's place), hurting whoever its `range` sweeps on the way.
                        // Nothing was listed for it (no InstantDamageInstance, no telegraph): it hit in 9 of runs 019-055 for 48-116, the
                        // hero 5-6 m from it as it began and 1.4-2.3 m after. A box along the dash from its wind-up; `left` is when the
                        // dash reaches the hero's point on it.
                        if (inst is Ai_Mon_Forest_BossDemon_Atk ba && ba.dash != null && ba.range != null)
                        {
                            float half = Mathf.Max(0.5f, RangeHalfWidth(ba.range));
                            float speed = ba.dash.duration > 0.01f ? ba.dash.distance / ba.dash.duration : 0f;
                            AddLine(list, h, radius, boss.position, Flat(boss.Control.desiredRotation * Vector3.forward).normalized, ba.dash.distance + half, half,
                                Mathf.Max(0f, ch.duration - ch.elapsedTime), speed, ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) : 1f, boss,
                                "Windup_" + t.GetType().Name + " (dash)");
                            continue;
                        }
                        // Iteration 50: Azurak's Atk. Its InitDamage spawns where Azurak stands, turned as he turns (desiredRotation:
                        // toward the point the attack is aimed at), and its `range` sits AHEAD of him in the prefab (~6 m). Read at
                        // Azurak's own place (below) it missed the blow: runs 047-054 drew it 7-11 m from the hero, and the blow
                        // landed 3.3-4.9 m from the hero (222-476 a run, and its trail after). The collider's place in the prefab,
                        // turned as the spawn is, is added here.
                        if (inst is Ai_Mon_Despair_BossAzurak_Atk_InitDamage az && az.range != null)
                        {
                            var rot = boss.Control.desiredRotation;
                            var off = Quaternion.Inverse(az.transform.rotation) * (az.range.transform.position - az.transform.position);
                            var at = boss.position + rot * new Vector3(off.x, 0f, off.z);
                            AddColliderStrike(list, h, radius, az.range, at, rot, Mathf.Max(0f, ch.duration - ch.elapsedTime + IdiDelay(az, boss)),
                                ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) : 1f, boss, "Windup_" + t.GetType().Name);
                            continue;
                        }
                        // (Primus's Grab: pulls whoever is in its range to it, slows them 100% for 1.5 s and resets its swipe.)
                        DewCollider col = inst is InstantDamageInstance idi ? idi.range
                                        : inst is Ai_Mon_Ink_BossWhiteNight_BuddhasPalm_Instance bp ? bp.range
                                        : inst is Ai_Mon_Primus_BossPrimusAeron_Force_Grab gr ? gr.range : null;
                        if (col == null) continue;
                        float extra = inst is Ai_Mon_Ink_BossWhiteNight_BuddhasPalm_Instance ? 0.1f : 0f;
                        float left = ch.duration - ch.elapsedTime + extra;
                        // A polygon (Primus's Force Atk and Swipe): placed as the instance will be - the facing it turns to
                        // (desiredRotation: where a Target / Point cast spawns it) and the collider's own place in the prefab;
                        // landing when the channel ends + the instance's own damageDelay.
                        bool polygon = col.shape == DewCollider.ColliderShape.Polygon;
                        if (polygon && inst is InstantDamageInstance pd) left += IdiDelay(pd, boss);
                        AddColliderStrike(list, h, radius, col, boss.position, polygon ? boss.Control.desiredRotation : boss.transform.rotation, Mathf.Max(0f, left),
                            ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / (ch.duration + extra)) : 1f, boss, "Windup_" + t.GetType().Name,
                            polygon ? inst.transform : null);
                    }
                }
                catch (Exception) { }
            }
            foreach (var id in JumpCast.Keys.Where(k => !liveJumps.Contains(k)).ToList()) JumpCast.Remove(id);

            // Iteration 54: the blows of ordinary monsters and minibosses from their wind-up (MonsterWindups), the Snow Wolf's Pounce
            // under way, the Soul Swordsman's SwiftStep and its slash, and Dark Moon's hallucinations' Blade (Monsters54).
            MonsterWindups(list, hero, h, radius);
            Monsters54(list, hero, h, radius);

            // Shots that blow up where they come down (a Dark Elemental's Barrage, the Orb Spitter's orbs, Stella
            // Matter, the Wretched Artillery's missiles, Erebos's Star Rain): a projectile with a `range` collider that
            // hurts everyone in it when it lands. Their red circle is drawn by a plain effect at the landing point, so
            // none of the readers above sees it; the projectile list gives only the shot's own small collision radius.
            // Listed as a strike at the point it flies to: the collider's radius, left = seconds until it lands at its
            // speed, fill = how far along its flight it is. type names the projectile.
            foreach (var p in Describe.Actors<Projectile>())
            {
                try
                {
                    if (p == null || !p.isActive || p.isCompleted || !Hostile(hero, p)) continue;
                    var col = LandingRange(p);
                    if (col == null) continue;
                    var target = p.mode == Projectile.ProjectileMode.Target ? p.Network_targetEntity : null;
                    var land = target != null ? target.position : p.Network_targetPosition;
                    float speed = p is StandardProjectile sp ? Mathf.Max(sp.targetSpeed, sp.initialSpeed, 0.1f) : 10f;
                    float left = Flat(land - p.position).magnitude / speed;
                    float fill = p is StandardProjectile sp2 ? Mathf.Clamp01(sp2.normalizedPosition) : 0f;
                    AddColliderStrike(list, h, radius, col, land, Quaternion.identity, left, fill, p.info.caster, p.GetType().Name);
                }
                catch (Exception) { }
            }

            // A blow a status effect deals when it ends: Nyx's phase change (she staggers, flies to the room's centre and
            // explodes there - 40% of the hero's health in run-040, twice, with nothing listed) and a miniboss's Unstable
            // Explosive (it explodes where it stands when the timer runs out). A strike: explodeRange at that place,
            // left = seconds until it goes off.
            foreach (var pc in Describe.Actors<Se_Mon_Sky_BossNyx_PhaseChange>())
            {
                try
                {
                    if (pc == null || !pc.isActive || pc.explodeRange == null) continue;
                    var nyx = pc.victim;
                    if (nyx == null || !Hostile(hero, nyx)) continue;
                    float at = pc.staggerDuration + pc.prepareExplodeDuration;
                    float left = at - (Time.time - pc.creationTime);
                    if (left < -0.1f) continue;
                    var roomCentre = SingletonBehaviour<Sky_BossRoomCenter>.instance;
                    var c = pc.displaceToCenter && roomCentre != null ? roomCentre.transform.position : nyx.agentPosition;
                    AddColliderStrike(list, h, radius, pc.explodeRange, c, Quaternion.identity, Mathf.Max(0f, left),
                        at > 0.01f ? Mathf.Clamp01(1f - left / at) : 1f, nyx, pc.GetType().Name);
                }
                catch (Exception) { }
            }
            foreach (var ue in Describe.Actors<Se_MiniBoss_UnstableExplosive_Explosion>())
            {
                try
                {
                    if (ue == null || !ue.isActive || ue.explodeRange == null) continue;
                    var carrier = ue.victim;
                    if (carrier == null || carrier.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, carrier)) continue;
                    float left = ue.remainingDuration ?? -1f;
                    if (left < 0f) continue;
                    AddColliderStrike(list, h, radius, ue.explodeRange, carrier.agentPosition, Quaternion.identity, left,
                        ue.explodeDelay > 0.01f ? Mathf.Clamp01(1f - left / ue.explodeDelay) : 1f, carrier, ue.GetType().Name);
                }
                catch (Exception) { }
            }

            // Ground that ticks damage for a while (TickDamageInstance: the Seeker's Tunnel Vision swarm, Dark Moon's spear
            // pool, White Night's Energy Wave, the Fire Devil room's tornadoes): a "strike" until its first tick (left =
            // seconds until then), then a "zone" until its last (left = seconds until that).
            foreach (var td in Describe.Actors<TickDamageInstance>())
            {
                try
                {
                    if (td == null || !td.isActive || td.range == null) continue;
                    var caster = td.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    float age = Time.time - td.creationTime;
                    bool before = age < td.delay;
                    float left = before ? td.delay - age : td.duration - age;
                    if (left < 0f) continue;
                    var col = td.range;
                    var t = col.transform;
                    var c = t.position + t.rotation * new Vector3(col.offset.x, 0f, col.offset.y);
                    float rad;
                    if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * t.lossyScale.x;
                    else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * t.lossyScale.x, col.size.y * t.lossyScale.z);
                    else continue;
                    float d = Flat(h - c).magnitude;
                    if (rad <= 0.05f || d > radius + rad) continue;
                    list.Add(new Area
                    {
                        shape = before ? "strike" : "zone",
                        by = Describe.EntityName(caster),
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = before ? R(td.delay > 0.01f ? Mathf.Clamp01(age / td.delay) : 1f) : 1f,
                        left = R(left),
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = td.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            // White Night's Destruction Wave (run-041's death: 224 + 41 + 43 + 43 at 7.3-7.9 m from her, nothing listed): she
            // lands, then after startDelay (+0.1) a box on her (`range`, moved onto her every frame) hurts every `interval` while
            // she turns maxRotationAngle (signed: toward the side the hero stood on) over rotationDuration, eased in and out, from
            // rotationDelay on. Listed as the slice still to be swept: centre her, radius the box's far corner, from where she
            // faces now through the rest of the turn (plus the box's own width); left = seconds until it starts hurting (0 while
            // it does), fill = how far through it is. A turn of a full circle or more is a "circle".
            foreach (var wv in Describe.Actors<Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave>())
            {
                try
                {
                    if (wv == null || !wv.isActive || wv.range == null) continue;
                    var wn = wv.info.caster;
                    if (wn == null || !Hostile(hero, wn)) continue;
                    float age = Time.time - wv.creationTime;
                    float on = wv.startDelay + 0.1f, turnAt = on + wv.rotationDelay, end = turnAt + wv.rotationDuration;
                    if (age > end + 0.1f) continue;
                    var col = wv.range;
                    // Iteration 49: the Wave's range is a polygon (the prefab: a spike from 0.59 m behind her to a point 17.5 m ahead,
                    // 5 m wide near her; rage x1.5 wide x2 long) - read above as a box of its unused size 1 x 1, a 0.71 m slice, while
                    // it hit at 7.3-8.0 m (run-041, run-051). Listed as three polys: the spike where it is now (left = until it hurts),
                    // the fan it sweeps through the rest of the turn (her place + an arc of the spike's length every <= 10 deg; a
                    // circle past a full turn; left = until the turn starts) and the spike where the turn ends (left = until no more
                    // than 30 deg are left) - in that order, all typed as the Wave.
                    if (col.shape == DewCollider.ColliderShape.Polygon && col.points != null && col.points.Length >= 3)
                    {
                        AddWavePolys(list, h, radius, wv, col, age, on, turnAt, end);
                        continue;
                    }
                    var sc = col.transform.lossyScale;
                    float far = (col.offset.y + col.size.y * 0.5f) * sc.z, halfW = (Mathf.Abs(col.offset.x) + col.size.x * 0.5f) * sc.x;
                    float rad = Mathf.Sqrt(far * far + halfW * halfW);
                    var trig = wn.Ability.GetAbility<At_Mon_Ink_BossWhiteNight_DestructionWave>();
                    bool turns = trig == null || !trig.disableRotation;
                    float t = wv.rotationDuration > 0.01f ? Mathf.Clamp01((age - turnAt) / wv.rotationDuration) : 1f;
                    float eased = t < 0.5f ? 2f * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) * 0.5f;
                    float rest = turns ? wv.maxRotationAngle * (1f - eased) : 0f;   // signed degrees still to turn
                    float width = far > 0.1f ? 2f * Mathf.Atan2(halfW, far) * Mathf.Rad2Deg : 180f;
                    float angle = Mathf.Abs(rest) + width;
                    var now = Flat(wn.transform.forward).normalized;
                    var fwd = Flat(Quaternion.AngleAxis(rest * 0.5f, Vector3.up) * now).normalized;
                    var c = wn.agentPosition;
                    var rel = Flat(h - c);
                    float d = rel.magnitude;
                    bool full = angle >= 359f;
                    if (d > radius + rad) continue;
                    bool inside = d <= rad && (full || d < 0.01f || Vector3.Angle(fwd, rel) <= angle * 0.5f);
                    list.Add(new Area
                    {
                        shape = full ? "circle" : "slice",
                        by = Describe.EntityName(wn),
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = R(Mathf.Min(angle, 360f)),
                        facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                        fill = R(end > 0.01f ? Mathf.Clamp01(age / end) : 1f),
                        left = R(Mathf.Max(0f, on - age)),
                        inside = inside,
                        edge = R(inside ? rad - d : 0f),
                        type = wv.GetType().Name,
                    });
                }
                catch (Exception) { }
            }

            Despair(list, hero, h, radius);
            Primus(list, hero, h, radius);

            return list.OrderBy(x => x.left).ToList();
        }

        // Iteration 49: White Night's Destruction Wave whose range is a polygon (see Telegraphs). rest = the signed degrees of her
        // turn still to come (Quaternion.AngleAxis about +Y, as the Wave turns her), from her facing now.
        private static void AddWavePolys(List<Area> list, Vector3 h, float radius, Ai_Mon_Ink_BossWhiteNight_DestructionWave_Wave wv,
            DewCollider col, float age, float on, float turnAt, float end)
        {
            var wn = wv.info.caster;
            var trig = wn.Ability.GetAbility<At_Mon_Ink_BossWhiteNight_DestructionWave>();
            bool turns = trig == null || !trig.disableRotation;
            float dur = wv.rotationDuration;
            float t = dur > 0.01f ? Mathf.Clamp01((age - turnAt) / dur) : 1f;
            float eased = t < 0.5f ? 2f * t * t : 1f - (-2f * t + 2f) * (-2f * t + 2f) * 0.5f;
            float rest = turns ? wv.maxRotationAngle * (1f - eased) : 0f;
            var now = Flat(wn.transform.forward).normalized;
            if (now.sqrMagnitude < 0.5f) return;
            var c = wn.agentPosition;
            float fill = end > 0.01f ? Mathf.Clamp01(age / end) : 1f;
            string type = wv.GetType().Name;
            var spike = PolyAt(col, null, c, Quaternion.LookRotation(now, Vector3.up));
            if (spike != null) AddPoly(list, h, radius, spike, now, on - age, fill, wn, type);
            if (Mathf.Abs(rest) <= 1f) return;
            var sc = col.transform.lossyScale;
            float far = col.points.Max(p => new Vector2(p.x * sc.x, p.y * sc.z).magnitude);
            if (Mathf.Abs(rest) >= 359f) AddCircle(list, h, radius, c, far, Mathf.Max(0f, turnAt - age), fill, wn, type);
            else
            {
                int n = Mathf.Max(2, Mathf.CeilToInt(Mathf.Abs(rest) / 10f));
                var fan = new Vector3[n + 2];
                fan[0] = c;
                for (int k = 0; k <= n; k++) fan[k + 1] = c + Quaternion.AngleAxis(rest * k / n, Vector3.up) * now * far;
                AddPoly(list, h, radius, fan, Flat(Quaternion.AngleAxis(rest * 0.5f, Vector3.up) * now).normalized, turnAt - age, fill, wn, type);
            }
            // When no more than 30 deg of the turn are left: eased(t) = 1 - 30 / |max|.
            float max = Mathf.Abs(wv.maxRotationAngle);
            float need = max > 30f ? 1f - 30f / max : 0f;
            float tEnd = need <= 0f ? 0f : need < 0.5f ? Mathf.Sqrt(need / 2f) : 1f - Mathf.Sqrt(2f * (1f - need)) / 2f;
            var endF = Flat(Quaternion.AngleAxis(rest, Vector3.up) * now).normalized;
            var endPoly = PolyAt(col, null, c, Quaternion.LookRotation(endF, Vector3.up));
            if (endPoly != null) AddPoly(list, h, radius, endPoly, endF, turnAt + tEnd * dur - age, fill, wn, type);
        }

        // ----- iteration 54: monsters' blows from their wind-up ----------------------------------------------------------
        // The costliest blows nothing listed in time (runs 030-056, bot.log "took"): the Snow Wolf's Pounce (a DashAttackInstance
        // like the Dread Bug's: 969 hp in 5 SnowMountain visits, never listed), Big Baam's beam (its box listed only from the beam's
        // start - the dash came at 0 s left: 171 / 153 / 152), the Soul Swordsman's SwiftStep (it vanishes, lands 1.5 m behind the
        // hero and slashes round its front 1.2 s / attack speed later, then shoots along the way the hero was: 208 from the slash,
        // 4 shots 51-102 - the hero leaves the slash straight out of its front, along the shots' line).

        // A collider's reach ahead of the place it is swept from (a box: its offset plus half its length; a circle: offset plus radius).
        private static float RangeFront(DewCollider col)
        {
            var sc = col.transform.lossyScale;
            if (col.shape == DewCollider.ColliderShape.Box) return Mathf.Max(0f, (col.offset.y + col.size.y * 0.5f) * sc.z);
            if (col.shape == DewCollider.ColliderShape.Circle) return Mathf.Max(0f, (col.offset.y + col.radius) * sc.x);
            return 1.5f;
        }

        // The farthest point of a collider from its origin (a polygon's farthest corner).
        private static float RangeFar(DewCollider col)
        {
            var sc = col.transform.lossyScale;
            if (col.shape == DewCollider.ColliderShape.Polygon && col.points != null && col.points.Length > 0)
                return col.points.Max(p => new Vector2(p.x * sc.x, p.y * sc.z).magnitude);
            if (col.shape == DewCollider.ColliderShape.Circle) return (col.offset.magnitude + col.radius) * sc.x;
            return new Vector2(Mathf.Abs(col.offset.x) + col.size.x * 0.5f, Mathf.Abs(col.offset.y) + col.size.y * 0.5f).magnitude * sc.x;
        }

        // Big Baam's beam as a box from o along fwd to its full reach: its growing tip hurts (a sphere cast of beamRadius over the
        // last hitBoxLength) as it passes, so `left` = wait + the time its tip needs to reach the hero's distance along it (the
        // distance curve over the beam's duration). Listed from the wind-up (wait = the channel left) as from the beam itself.
        private static void AddBeam(List<Area> list, Vector3 h, float radius, Vector3 o, Vector3 fwd, Ai_Mon_Sky_BigBaam_BeamAtk bm, float dur,
            float wait, float fill, Entity caster, string type)
        {
            float reach = bm.distanceCurve.Evaluate(1f);
            float along = Vector3.Dot(Flat(h - o), fwd);
            float t = 1f;
            for (int i = 0; i <= 40; i++) { float k = i / 40f; if (bm.distanceCurve.Evaluate(k) >= along) { t = k; break; } }
            AddLine(list, h, radius, o, fwd, reach, Mathf.Max(0.1f, bm.beamRadius), wait + t * dur, 0f, fill, caster, type);
        }

        private static void MonsterWindups(List<Area> list, Hero hero, Vector3 h, float radius)
        {
            foreach (var m in Describe.Actors<Monster>())
            {
                try
                {
                    if (m == null || !m.isActive || !m.isAlive || m.type.ToString() == "Boss" || !Hostile(hero, m)) continue;
                    if (Flat(m.position - h).magnitude > radius + 16f) continue;
                    Channel ch = null;
                    foreach (var c in m.Control.ongoingChannels)
                        if (c != null && c.isAlive && c.duration > 0.05f && (ch == null || c.duration - c.elapsedTime > ch.duration - ch.elapsedTime)) ch = c;
                    if (ch == null) continue;
                    float chLeft = Mathf.Max(0f, ch.duration - ch.elapsedTime), chFill = ch.duration > 0.01f ? Mathf.Clamp01(ch.elapsedTime / ch.duration) : 1f;
                    bool mini = m.type.ToString() == "MiniBoss";
                    var rot = m.Control.desiredRotation;
                    var fwd = Flat(rot * Vector3.forward).normalized;
                    foreach (var t in m.Ability.abilities.Values)
                    {
                        if (t == null || !t.Network_isCasting) continue;
                        var inst = t.currentConfig.spawnedInstance;
                        // The Snow Wolf's Pounce: at the channel's end it dashes dash.distance (x1.5 as a miniboss: Mon_SnowMountain_SnowWolf.
                        // OnCreateAsMiniBoss) toward the point it was cast at, its `range` (a 1.25 x 2.25 m box 1.5 m ahead) sweeping the way.
                        // A lane from the wolf along where it turns: the dash + the range's reach ahead, the range's half width to each side;
                        // `left` = the channel left + when the dash reaches the hero's point on it.
                        if (inst is Ai_Mon_SnowMountain_SnowWolf_Pounce pw && pw.dash != null && pw.range != null)
                        {
                            float dist = pw.dash.distance * (mini && m is Mon_SnowMountain_SnowWolf ? 1.5f : 1f);
                            float speed = pw.dash.duration > 0.01f ? dist / pw.dash.duration : 0f;
                            AddLine(list, h, radius, m.agentPosition, fwd, dist + RangeFront(pw.range), Mathf.Max(0.5f, RangeHalfWidth(pw.range)),
                                chLeft, speed, chFill, m, "Windup_" + t.GetType().Name + " (dash)");
                            continue;
                        }
                        // Big Baam's beam (its attack): at the channel's end a beam along the cast's direction from startOffset (three,
                        // 15 deg apart, as a miniboss: At_Mon_Sky_BigBaam_BeamAtk.spawnAdditionalProjectile), its tip growing out over
                        // beamDuration x attack speed (Ai_..._BeamAtk.OnPrepare).
                        if (inst is Ai_Mon_Sky_BigBaam_BeamAtk bm && bm.distanceCurve != null)
                        {
                            float dur = bm.beamDuration * Mathf.Max(0.01f, m.Status.attackSpeedMultiplier);
                            var o = m.position + rot * bm.startOffset;
                            bool three = t is At_Mon_Sky_BigBaam_BeamAtk bt && bt.spawnAdditionalProjectile;
                            foreach (float off in three ? new[] { 15f, 0f, -15f } : new[] { 0f })
                                AddBeam(list, h, radius, o, Flat(Quaternion.Euler(0f, off, 0f) * fwd).normalized, bm, dur, chLeft, chFill * 0.5f, m,
                                    "Windup_" + t.GetType().Name);
                            continue;
                        }
                        // The Soul Swordsman's SwiftStep: after the channel it vanishes and lands backDistance behind the hero (on the far
                        // side from itself) in `duration`, then slashes round its front (SwiftStep_Atk: castDuration / attack speed).
                        // A strike round where it will land (the slash's reach), which follows the hero until the step starts.
                        if (inst is Ai_Mon_Ink_GhostBlade_SwiftStep ss)
                        {
                            var atk = DewResources.GetByType<Ai_Mon_Ink_GhostBlade_SwiftStep_Atk>();
                            if (atk == null || atk.range == null) continue;
                            var away = Flat(h - m.agentPosition);
                            var land = h + (away.sqrMagnitude > 0.01f ? away.normalized : fwd) * ss.backDistance;
                            AddCircle(list, h, radius, land, RangeFar(atk.range), chLeft + ss.duration + atk.castDuration / Mathf.Max(0.1f, m.Status.attackSpeedMultiplier),
                                chFill * 0.3f, m, "Windup_" + t.GetType().Name);
                        }
                    }
                }
                catch (Exception) { }
            }
        }

        private static void Monsters54(List<Area> list, Hero hero, Vector3 h, float radius)
        {
            // The Snow Wolf's Pounce under way: a lane from where it is to where its dash ends, as far as its range reaches ahead.
            foreach (var pw in Describe.Actors<Ai_Mon_SnowMountain_SnowWolf_Pounce>()) DashUnderWay(list, hero, h, radius, pw, true);

            // The SwiftStep under way (the swordsman unseen, on its way to backDistance behind the hero): the strike round where the
            // step ends (its displacement's destination), landing when the slash does.
            Ai_Mon_Ink_GhostBlade_SwiftStep_Atk atkPrefab = null;
            try { atkPrefab = DewResources.GetByType<Ai_Mon_Ink_GhostBlade_SwiftStep_Atk>(); } catch (Exception) { }
            foreach (var ss in Describe.Actors<Ai_Mon_Ink_GhostBlade_SwiftStep>())
            {
                try
                {
                    if (ss == null || !ss.isActive || atkPrefab == null || atkPrefab.range == null) continue;
                    var sw = ss.info.caster;
                    if (sw == null || sw.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, sw)) continue;
                    if (!(sw.Control.ongoingDisplacement is DispByDestination dd)) continue;
                    float cast = atkPrefab.castDuration / Mathf.Max(0.1f, sw.Status.attackSpeedMultiplier);
                    float left = Mathf.Max(0f, dd.duration - dd.elapsedTime) + cast;
                    AddCircle(list, h, radius, dd.destination, RangeFar(atkPrefab.range), left, Mathf.Clamp01(1f - left / Mathf.Max(0.1f, dd.duration + cast)), sw, ss.GetType().Name);
                }
                catch (Exception) { }
            }

            // Its slash: the Atk's `range` (a 4 m pie round its front, 10 points: the bundle) at the swordsman, turned to the angle it
            // was given (toward the hero as it landed), hurting castDuration / attack speed after the Atk began; and the shots it
            // fires then (addedProjectiles + 1, 20 deg apart, along that angle) as lines of their collision radius, from the
            // swordsman as far as they fly, `left` = the slash + their flight to the hero's point on them.
            Ai_Mon_Ink_GhostBlade_SwiftStep_Projectile shot = null;
            try { shot = DewResources.GetByType<Ai_Mon_Ink_GhostBlade_SwiftStep_Projectile>(); } catch (Exception) { }
            foreach (var sa in Describe.Actors<Ai_Mon_Ink_GhostBlade_SwiftStep_Atk>())
            {
                try
                {
                    if (sa == null || !sa.isActive || sa.range == null) continue;
                    var sw = sa.info.caster;
                    if (sw == null || sw.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, sw)) continue;
                    float cast = sa.castDuration / Mathf.Max(0.1f, sw.Status.attackSpeedMultiplier);
                    float left = cast - (Time.time - sa.creationTime);
                    if (left < -0.05f) continue;
                    float fill = cast > 0.01f ? Mathf.Clamp01(1f - left / cast) : 1f;
                    var at = sw.agentPosition;
                    var rot = Quaternion.Euler(0f, sa.info.angle, 0f);
                    if (sa.range.shape == DewCollider.ColliderShape.Polygon)
                    {
                        var pie = PolyAt(sa.range, null, at, rot);
                        if (pie != null) AddPoly(list, h, radius, pie, Flat(rot * Vector3.forward).normalized, Mathf.Max(0f, left), fill, sw, sa.GetType().Name);
                    }
                    else AddCircle(list, h, radius, at, RangeFar(sa.range), Mathf.Max(0f, left), fill, sw, sa.GetType().Name);
                    if (shot == null) continue;
                    int n = sa.addedProjectiles + 1;
                    float speed = Mathf.Max(shot.targetSpeed, shot.initialSpeed, 0.1f);
                    for (int i = 0; i < n; i++)
                    {
                        float ang = sa.info.angle - 20f * (n - 1) * 0.5f + 20f * i;
                        AddLine(list, h, radius, at, Flat(Quaternion.Euler(0f, ang, 0f) * Vector3.forward).normalized, shot.startInFrontDistance + shot.endDistance,
                            Mathf.Max(0.25f, shot.collisionRadius), Mathf.Max(0f, left), speed, fill, sw, shot.GetType().Name + " (line)");
                    }
                }
                catch (Exception) { }
            }

            // Dark Moon's hallucinations' Blade (rage: Ai_Mon_Ink_BossDarkMoon_Blade_RageInstance, an AbilityInstance, not an IDI - so
            // never listed; 117 in run-030 and in run-038): its `range` (a 14-point shape, the bundle) placed where the instance is,
            // hurting startDelay after it began.
            foreach (var ri in Describe.Actors<Ai_Mon_Ink_BossDarkMoon_Blade_RageInstance>())
            {
                try
                {
                    if (ri == null || !ri.isActive || ri.range == null) continue;
                    var hc = ri.info.caster;
                    if (hc == null || !Hostile(hero, hc)) continue;
                    float left = ri.startDelay - (Time.time - ri.creationTime);
                    if (left < -0.05f) continue;
                    float fill = ri.startDelay > 0.01f ? Mathf.Clamp01(1f - left / ri.startDelay) : 1f;
                    if (ri.range.shape == DewCollider.ColliderShape.Polygon)
                    {
                        var poly = PolyAt(ri.range, null, ri.transform.position, ri.transform.rotation);
                        if (poly != null) AddPoly(list, h, radius, poly, Flat(ri.transform.forward).normalized, Mathf.Max(0f, left), fill, hc, ri.GetType().Name);
                    }
                    else AddRange(list, h, radius, ri.range, Mathf.Max(0f, left), fill, hc, ri.GetType().Name);
                }
                catch (Exception) { }
            }
        }

        // ----- zone 3 (Despair: Azurak and its rooms) ------------------------------------------------

        private static readonly FieldInfo RollSpeedField = AccessTools.Field(typeof(Ai_Mon_Despair_BossAzurak_Roll), "_nextRollSpeed");

        private static void Despair(List<Area> list, Hero hero, Vector3 h, float radius)
        {
            // Azurak's Roll: he rolls across the arena and out the other side (radius + 25 past its centre), hitting and
            // knocking back whoever is within hitRadius of him, again and again (15 -> 30 m/s) until every roll pillar is
            // down; then he burrows (invulnerable) and comes up at the arena's centre. A box along the roll from where he is:
            // width 2 x hitRadius, left = seconds until he reaches the hero's point along it. Before the first roll (1.5 s of
            // winding up) the same box along his facing; between rolls (he is gone, then teleported outside the arena and
            // rolls at once) nothing.
            foreach (var ro in Describe.Actors<Ai_Mon_Despair_BossAzurak_Roll>())
            {
                try
                {
                    if (ro == null || !ro.isActive) continue;
                    var az = ro.info.caster;
                    if (az == null || az.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, az)) continue;
                    float age = Time.time - ro.creationTime;
                    bool rolling = az.Control.isDisplacing;
                    if (!rolling && age > 1.5f) continue;
                    float speed = rolling && RollSpeedField != null ? Mathf.Max(1f, (float)RollSpeedField.GetValue(ro)) : Mathf.Max(1f, ro.initRollSpeed);
                    float wait = rolling ? 0f : 1.5f - age;
                    var arena = SingletonBehaviour<Room_BossArena>.instance;
                    float reach = arena != null ? 2f * (arena.radius + 25f) : 60f;
                    AddLine(list, h, radius, az.agentPosition, Flat(az.transform.forward).normalized, reach, Mathf.Max(0.5f, ro.hitRadius),
                        wait, speed, rolling ? 1f : Mathf.Clamp01(age / 1.5f), az, ro.GetType().Name);
                }
                catch (Exception) { }
            }

            // Azurak's Roar: pillars rise round the arena (pillarSpawnDelay, then one every 0.1 s), and after atkDelay he roars
            // atkCount times, atkInterval apart: everyone within 50 m without the SafeZone status is hit and knocked back. The
            // status is given to whoever stands in a SafeZoneSpawner's range - one on each pillar and on each monster spawner,
            // turned to face away from him (the pillar's shadow). Listed as "safe" circles (a box or polygon as the circle
            // inside it); left = seconds until the next roar. Listed until the last roar.
            foreach (var ra in Describe.Actors<Ai_Mon_Despair_BossAzurak_RoarAtk>())
            {
                try
                {
                    if (ra == null || !ra.isActive) continue;
                    var az = ra.info.caster;
                    if (az == null || !Hostile(hero, az)) continue;
                    float first = ra.creationTime + ra.pillarSpawnDelay + ra.pillarCount * 0.1f + ra.atkDelay;
                    float last = first + Mathf.Max(0, ra.atkCount - 1) * ra.atkInterval;
                    float now = Time.time;
                    if (now > last + 0.2f) continue;
                    float next = first;
                    if (now > first && ra.atkInterval > 0.01f) next = Mathf.Min(last, first + Mathf.Ceil((now - first) / ra.atkInterval) * ra.atkInterval);
                    float left = Mathf.Max(0f, next - now);
                    foreach (var sz in Describe.Actors<Se_Mon_Despair_BossAzurak_RoarAtk_SafeZoneSpawner>())
                    {
                        if (sz == null || !sz.isActive || sz.range == null || sz.info.caster != az) continue;
                        if (sz.victim == null || sz.victim.IsNullInactiveDeadOrKnockedOut()) continue;
                        if (!InsideCircle(sz.range, out var c, out var rad) || rad <= 0.05f) continue;
                        float d = Flat(h - c).magnitude;
                        if (d > radius + rad + 30f) continue;
                        list.Add(new Area
                        {
                            shape = "safe",
                            by = Describe.EntityName(az),
                            centre = Describe.Vec(c),
                            radius = R(rad),
                            angle = 360f,
                            fill = R(Mathf.Clamp01((now - ra.creationTime) / Mathf.Max(0.1f, first - ra.creationTime))),
                            left = R(left),
                            inside = d <= rad,
                            edge = R(Mathf.Max(0f, rad - d)),
                            type = sz.GetType().Name,
                        });
                    }
                }
                catch (Exception) { }
            }

            // Azurak's Double Stomp: waveCount waves of two stomps, each marking a circle (radius) at where each hero will be;
            // none hurts until the final stomp, when every circle marked goes off at once (startDelay + waveCount x (2 x
            // doubleAtkInterval + waveInterval) + finalAtkDelay after the cast).
            var stomps = Describe.Actors<Ai_Mon_Despair_BossAzurak_DoubleStomp>().Where(x => x != null && x.isActive).ToList();
            foreach (var si in Describe.Actors<Ai_Mon_Despair_BossAzurak_DoubleStomp_Instance>())
            {
                try
                {
                    if (si == null || !si.isActive) continue;
                    var az = si.info.caster;
                    if (az == null || !Hostile(hero, az)) continue;
                    var ds = stomps.FirstOrDefault(x => x.info.caster == az);
                    if (ds == null) continue;
                    float at = ds.creationTime + ds.startDelay + ds.waveCount * (2f * ds.doubleAtkInterval + ds.waveInterval) + ds.finalAtkDelay;
                    float left = at - Time.time;
                    if (left < -0.1f) continue;
                    AddCircle(list, h, radius, si.info.point, si.radius, Mathf.Max(0f, left),
                        Mathf.Clamp01((Time.time - ds.creationTime) / Mathf.Max(0.1f, at - ds.creationTime)), az, si.GetType().Name);
                }
                catch (Exception) { }
            }

            // Azurak's Stomp Projectile: a blow at the point (its `range`) dmgDelay after the telegraph, then shots out from it
            // (listed as projectiles once they fly).
            foreach (var sp in Describe.Actors<Ai_Mon_Despair_BossAzurak_StompProjectile>())
            {
                try
                {
                    if (sp == null || !sp.isActive || sp.range == null) continue;
                    var az = sp.info.caster;
                    if (az != null && !Hostile(hero, az)) continue;
                    float left = sp.dmgDelay - (Time.time - sp.creationTime);
                    if (left < -0.1f) continue;
                    AddColliderStrike(list, h, radius, sp.range, sp.info.point, Quaternion.identity, Mathf.Max(0f, left),
                        sp.dmgDelay > 0.01f ? Mathf.Clamp01(1f - left / sp.dmgDelay) : 1f, az, sp.GetType().Name);
                }
                catch (Exception) { }
            }

            // Azurak's Stomp Block: waveCount waves; each turns to the nearest hero, stomps round itself (stompRange) after
            // prepareDuration, then blocks from the front for waveDuration while lobbing artillery. The stomp as a strike while
            // it winds up; each artillery shell as a strike where it comes down (the Artillery_Damage's range there).
            foreach (var sb in Describe.Actors<Ai_Mon_Despair_BossAzurak_StompBlock>())
            {
                try
                {
                    if (sb == null || !sb.isActive || sb.stompRange == null) continue;
                    var az = sb.info.caster;
                    if (az == null || !Hostile(hero, az)) continue;
                    float period = sb.prepareDuration + sb.waveDuration + 0.5f;
                    float age = Time.time - sb.creationTime;
                    int wave = period > 0.01f ? Mathf.FloorToInt(age / period) : 0;
                    float inWave = age - wave * period;
                    if (wave >= sb.waveCount || inWave > sb.prepareDuration) continue;
                    float left = sb.prepareDuration - inWave;
                    var t = sb.stompRange.transform;
                    AddColliderStrike(list, h, radius, sb.stompRange, t.position, t.rotation, left,
                        sb.prepareDuration > 0.01f ? Mathf.Clamp01(inWave / sb.prepareDuration) : 1f, az, sb.GetType().Name);
                }
                catch (Exception) { }
            }
            var shell = DewResources.GetByType<Ai_Mon_Despair_BossAzurak_StompBlock_Artillery_Damage>();
            foreach (var ar in Describe.Actors<Ai_Mon_Despair_BossAzurak_StompBlock_Artillery>())
            {
                try
                {
                    if (shell == null || shell.range == null || ar == null || !ar.isActive || ar.isCompleted || !Hostile(hero, ar)) continue;
                    var land = ar.Network_targetPosition;
                    float speed = Mathf.Max(ar.targetSpeed, ar.initialSpeed, 0.1f);
                    float left = Flat(land - ar.position).magnitude / speed;
                    AddColliderStrike(list, h, radius, shell.range, land, Quaternion.identity, left, Mathf.Clamp01(ar.normalizedPosition), ar.info.caster, ar.GetType().Name);
                }
                catch (Exception) { }
            }

            // A Displacer's egg (after its blink, or cast): it hatches after delay + duration and blows up in its `range`.
            foreach (var eg in Describe.Actors<Ai_Mon_Despair_Displacer_SpawnEgg>())
            {
                try
                {
                    if (eg == null || !eg.isActive || eg.range == null) continue;
                    var caster = eg.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float total = eg.delay + eg.duration;
                    float left = total - (Time.time - eg.creationTime);
                    if (left < -0.1f) continue;
                    var t = eg.range.transform;
                    AddColliderStrike(list, h, radius, eg.range, t.position, t.rotation, Mathf.Max(0f, left),
                        total > 0.01f ? Mathf.Clamp01(1f - left / total) : 1f, caster, eg.GetType().Name);
                }
                catch (Exception) { }
            }

            // Iteration 41 (run-049's death: 280 from a Phase Bug's dash, nothing listed): a Displacer's dash under way
            // (Ai_Mon_Despair_Displacer_Dash, a DashAttackInstance: dash.distance along the cast's direction, hurting whoever its
            // `range` sweeps). A box from where it is to where the displacement ends, `left` 0 (it is on the hero's line now).
            foreach (var da in Describe.Actors<Ai_Mon_Despair_Displacer_Dash>())
            {
                try
                {
                    if (da == null || !da.isActive || da.range == null) continue;
                    var caster = da.info.caster;
                    if (caster == null || caster.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, caster)) continue;
                    var disp = da.currentDisplacement;
                    if (disp == null || !caster.Control.isDisplacing) continue;
                    var o = caster.agentPosition;
                    var to = Flat(disp.destination - o);
                    if (to.magnitude < 0.2f) continue;
                    AddLine(list, h, radius, o, to.normalized, to.magnitude + RangeHalfWidth(da.range), Mathf.Max(0.5f, RangeHalfWidth(da.range)),
                        0f, 0f, 1f, caster, da.GetType().Name);
                }
                catch (Exception) { }
            }

            // Iteration 48 (run-053's death in Despair: 4 Dread Bug dash attacks 139/139/139/109 and the Unstable Rat's 48s, none
            // listed): their attacks are DashAttackInstances too (Ai_Mon_Despair_DreadBug_Atk, Ai_Mon_Despair_UnstableRat_Atk: the
            // caster dashes at the cast's point, hurting whoever its `range` sweeps). Under way: a box from where the caster is to
            // where its displacement ends, as the Displacer's above; typed.
            foreach (var da in Describe.Actors<Ai_Mon_Despair_DreadBug_Atk>()) DashUnderWay(list, hero, h, radius, da);
            foreach (var da in Describe.Actors<Ai_Mon_Despair_UnstableRat_Atk>()) DashUnderWay(list, hero, h, radius, da);
            // Iteration 52: Belphomet's Atk under way, the same way (its wind-up is a box in the zone boss's reader).
            foreach (var da in Describe.Actors<Ai_Mon_Forest_BossDemon_Atk>()) DashUnderWay(list, hero, h, radius, da);

            // The Wretched Artillery's missiles leave burning ground where they land (BarrageAtk_AoE: radius, tickCount x
            // tickInterval): a "zone" while it ticks.
            foreach (var ae in Describe.Actors<Ai_Mon_Despair_WretchedArtillery_BarrageAtk_AoE>())
            {
                try
                {
                    if (ae == null || !ae.isActive) continue;
                    var caster = ae.info.caster;
                    bool friendly = false;
                    try { friendly = caster != null && hero.GetRelation(caster) != EntityRelation.Enemy; } catch (Exception) { }
                    if (friendly) continue;
                    float left = ae.tickCount * ae.tickInterval - (Time.time - ae.creationTime);
                    if (left < 0f) continue;
                    var c = ae.position;
                    float d = Flat(h - c).magnitude;
                    if (ae.radius <= 0.05f || d > radius + ae.radius) continue;
                    list.Add(new Area
                    {
                        shape = "zone",
                        by = caster != null ? Describe.EntityName(caster) : null,
                        centre = Describe.Vec(c),
                        radius = R(ae.radius),
                        angle = 360f,
                        fill = 1f,
                        left = R(left),
                        inside = d <= ae.radius,
                        edge = R(Mathf.Max(0f, ae.radius - d)),
                        type = ae.GetType().Name,
                    });
                }
                catch (Exception) { }
            }
        }

        // Iteration 48: a monster's dash attack under way (a DashAttackInstance whose caster is displacing): a line box from the
        // caster to where the displacement ends, as wide as its `range`, `left` 0 (it is on its way now). Read only.
        // Iteration 54: front - the lane runs on past where the dash ends as far as the range reaches ahead (RangeFront; the Snow
        // Wolf's Pounce: a box 1.5 m ahead, 2.25 m long), not only its half width.
        private static void DashUnderWay(List<Area> list, Hero hero, Vector3 h, float radius, DashAttackInstance da, bool front = false)
        {
            try
            {
                if (da == null || !da.isActive || da.range == null) return;
                var caster = da.info.caster;
                if (caster == null || caster.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, caster)) return;
                var disp = da.currentDisplacement;
                if (disp == null || !caster.Control.isDisplacing) return;
                var o = caster.agentPosition;
                var to = Flat(disp.destination - o);
                if (to.magnitude < 0.2f) return;
                float half = RangeHalfWidth(da.range);
                AddLine(list, h, radius, o, to.normalized, to.magnitude + (front ? RangeFront(da.range) : half), Mathf.Max(0.5f, half), 0f, 0f, 1f, caster, da.GetType().Name);
            }
            catch (Exception) { }
        }

        // ----- iteration 41: the Phase Bug (Mon_Despair_Displacer) and a miniboss's spinning arrows ------------------------
        // run-049 died to a miniboss Phase Bug in Despair's first room: its Dash (280) while the hero stood charging a shot, then
        // two of its Spinning Arrows (346, 332). Decompiled (history/it41): the Displacer casts its Dash (At_..._Dash; a miniboss
        // has 3 charges, the channel halved) whenever the hero is in the trigger's range, dashing dash.distance along the cast's
        // direction (fixed at the cast's start); it Blinks (invulnerable, unseen) toward the hero and lays an egg where it lands.
        // Se_MiniBoss_SpinningArrow shoots an arrow every shootInterval from the carrier, the direction turning angleStep each
        // time (a full turn ~5.5 s), after startDelay, the first facing away from the nearest hero; none while the carrier
        // dashes (disableOnDash), spawns or is unseen, nor for disableArrowsDuration after. An arrow's first hit on an entity in
        // 2 s deals 1.65x, the others 0.25x.

        private const string DashWindup = "Windup_At_Mon_Despair_Displacer_Dash";

        // The type given to a box drawn by a Displacer winding its dash up; null for anything else.
        private static string DashWindupType(Entity caster)
        {
            try
            {
                if (!(caster is Mon_Despair_Displacer dp)) return null;
                var t = dp.Ability.GetAbility<At_Mon_Despair_Displacer_Dash>();
                return t != null && t.Network_isCasting ? DashWindup : null;
            }
            catch (Exception) { return null; }
        }

        // The Displacers within radius: where each is, how far its dash goes (reach: the dash's distance + its collider's half
        // width; range: the trigger's cast range - the AI dashes when the hero is within it), its charges, whether it can dash
        // now, whether it is winding one up (and the seconds left of that), whether it is a miniboss, and whether it can be hurt.
        private static List<object> Dashers(Hero hero, Vector3 h, float radius)
        {
            var list = new List<object>();
            Ai_Mon_Despair_Displacer_Dash prefab = null;
            try { prefab = DewResources.GetByType<Ai_Mon_Despair_Displacer_Dash>(); } catch (Exception) { }
            foreach (var dp in Describe.Actors<Mon_Despair_Displacer>())
            {
                try
                {
                    if (dp == null || dp.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, dp)) continue;
                    var p = dp.agentPosition;
                    if (Flat(p - h).magnitude > radius + 12f) continue;
                    var t = dp.Ability.GetAbility<At_Mon_Despair_Displacer_Dash>();
                    if (t == null) continue;
                    float half = prefab != null && prefab.range != null ? RangeHalfWidth(prefab.range) : 1f;
                    float dist = prefab != null && prefab.dash != null ? prefab.dash.distance : 0f;
                    float windup = -1f;
                    if (t.Network_isCasting)
                        foreach (var c in dp.Control.ongoingChannels)
                            if (c != null && c.isAlive) windup = Mathf.Max(windup, c.duration - c.elapsedTime);
                    list.Add(new
                    {
                        id = dp.netId,
                        by = Describe.EntityName(dp),
                        position = Describe.Vec(p),
                        reach = R(dist + half),
                        half = R(half),
                        range = R(t.currentConfig.effectiveRange),
                        charges = t.currentConfigCurrentCharge,
                        maxCharges = t.currentConfig.maxCharges,
                        ready = t.CanBeCast(),
                        casting = t.Network_isCasting,
                        windupLeft = R(windup),
                        channel = R(t.currentConfig.channel.duration),
                        miniBoss = dp.type.ToString() == "MiniBoss",
                        invulnerable = dp.Status.hasInvulnerable,
                    });
                }
                catch (Exception) { }
            }
            return list;
        }

        private static readonly FieldInfo SpinSetupField = AccessTools.Field(typeof(Se_MiniBoss_SpinningArrow), "_didSetup");
        private static readonly FieldInfo SpinLastShotField = AccessTools.Field(typeof(Se_MiniBoss_SpinningArrow), "_lastShootTime");
        private static readonly FieldInfo SpinDisableField = AccessTools.Field(typeof(Se_MiniBoss_SpinningArrow), "_disableTime");

        // The spinning arrows round each hostile carrier within radius: centre (the carrier - each arrow starts there), facing
        // (the direction of the last arrow shot), turn (+1: each next arrow turns counter-clockwise seen from above, i.e. the
        // angle atan2(z, x) grows; -1 the other way), step (degrees an arrow), interval (s an arrow), next (s until the next
        // arrow), started (false: startsIn s until the first), pausedFor (s the arrows are held: the carrier dashed, blinked or
        // spawned), and each arrow's speed, collision radius and reach (from the carrier's centre to where it stops).
        private static List<object> Spinners(Hero hero, Vector3 h, float radius)
        {
            var list = new List<object>();
            Ai_MiniBoss_SpinningArrow_Arrow arrow = null;
            try { arrow = DewResources.GetByType<Ai_MiniBoss_SpinningArrow_Arrow>(); } catch (Exception) { }
            foreach (var sa in Describe.Actors<Se_MiniBoss_SpinningArrow>())
            {
                try
                {
                    if (sa == null || !sa.isActive) continue;
                    var v = sa.victim;
                    if (v == null || v.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, v)) continue;
                    var c = v.position;
                    float speed = arrow != null ? Mathf.Max(arrow.targetSpeed, arrow.initialSpeed, 0.1f) : 0f;
                    float reach = arrow != null ? arrow.startInFrontDistance + arrow.endDistance : 0f;
                    if (Flat(c - h).magnitude > radius + reach) continue;
                    float now = Time.time;
                    bool started = SpinSetupField != null && (bool)SpinSetupField.GetValue(sa);
                    float last = SpinLastShotField != null ? (float)SpinLastShotField.GetValue(sa) : 0f;
                    float off = SpinDisableField != null ? (float)SpinDisableField.GetValue(sa) : 0f;
                    var fwd = Quaternion.Euler(0f, sa.Network_angle, 0f) * Vector3.forward;
                    bool unseen = v.Visual.isSpawning || v.Visual.isRendererOff || (sa.disableOnDash && v.Control.isDashing);
                    list.Add(new
                    {
                        id = sa.netId,
                        by = Describe.EntityName(v),
                        carrier = v.netId,
                        centre = Describe.Vec(c),
                        facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                        turn = sa.angleStep > 0f ? -1 : 1,   // Unity's yaw grows clockwise seen from above
                        step = R(Mathf.Abs(sa.angleStep)),
                        interval = R(sa.shootInterval),
                        next = R(started ? Mathf.Max(0f, last + sa.shootInterval - now) : -1f),
                        started,
                        startsIn = R(started ? 0f : Mathf.Max(0f, sa.startDelay - (now - sa.creationTime))),
                        pausedFor = R(unseen ? Mathf.Max(sa.disableArrowsDuration, off - now) : Mathf.Max(0f, off - now)),
                        speed = R(speed),
                        arrowRadius = R(arrow != null ? arrow.collisionRadius : 0f),
                        reach = R(reach),
                    });
                }
                catch (Exception) { }
            }
            return list;
        }

        // ----- zone 4 (Primus) -----------------------------------------------------------------------

        private static void Primus(List<Area> list, Hero hero, Vector3 h, float radius)
        {
            // A giant sword dropped on a floor piece (Force, and the phase changes): when it lands (damageDelay) the piece
            // breaks - whoever stands on it loses half their health + shield, is stunned and thrown to 6 m round the arena's
            // centre (Primus_Pizza0.Break, Se_Primus_Pizza_Fly). The piece's own range: "poly" (corners = its outline) or a
            // circle / box; left = seconds until the sword lands. The sword's own blow is a strike above (InstantDamageInstance).
            foreach (var gs in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Force_DropGiantSword>())
            {
                try
                {
                    if (gs == null || !gs.isActive) continue;
                    var pz = gs.pizza;
                    if (pz == null || !pz.isActive || pz.isBroken || pz.range == null) continue;
                    var caster = gs.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float left = gs.damageDelay - (Time.time - gs.creationTime);
                    if (left < -0.1f) continue;
                    AddRange(list, h, radius, pz.range, Mathf.Max(0f, left), gs.damageDelay > 0.01f ? Mathf.Clamp01(1f - left / gs.damageDelay) : 1f, caster, "Primus_Pizza0");
                }
                catch (Exception) { }
            }

            // The Gold Rain's slash (Force < 35%): an InstantDamageInstance where Primus stands, then subInstances blows
            // subInstanceGap apart along its angle, one every subInstanceInterval. A box along that line (the blows not out yet);
            // left = seconds until the first of it lands.
            var sub = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Force_GoldRain_Attack_SubInstance>();
            foreach (var ga in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Force_GoldRain_Attack>())
            {
                try
                {
                    if (ga == null || !ga.isActive) continue;
                    var caster = ga.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    float age = Time.time - ga.creationTime;
                    float subDelay = sub != null ? sub.damageDelay : 0.5f;
                    float end = ga.subInstances * ga.subInstanceInterval + subDelay;
                    if (age > end + 0.1f) continue;
                    float half = sub != null && sub.range != null ? RangeHalfWidth(sub.range) : 1.5f;
                    var fwd = Flat(ga.info.rotation * Vector3.forward).normalized;
                    float left = Mathf.Max(0f, Mathf.Min(ga.damageDelay - age, ga.subInstanceInterval + subDelay - age));
                    AddLine(list, h, radius, ga.position, fwd, (ga.subInstances + 0.5f) * ga.subInstanceGap, half, left, 0f,
                        Mathf.Clamp01(age / Mathf.Max(0.1f, end)), caster, ga.GetType().Name);
                }
                catch (Exception) { }
            }

            // The Jump Attack: Primus leaps to the point (dashDuration) and lands there - a circle and five turning cones. The
            // circle's range at the point, as a strike landing when the leap ends.
            var jumpCircle = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_CircleInstance>();
            foreach (var ja in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack>())
            {
                try
                {
                    if (jumpCircle == null || jumpCircle.range == null || ja == null || !ja.isActive) continue;
                    var caster = ja.info.caster;
                    if (caster == null || !Hostile(hero, caster)) continue;
                    float left = ja.dashDuration - (Time.time - ja.creationTime);
                    if (left < -0.1f) continue;
                    AddColliderStrike(list, h, radius, jumpCircle.range, ja.info.point, Quaternion.identity, Mathf.Max(0f, left),
                        ja.dashDuration > 0.01f ? Mathf.Clamp01(1f - left / ja.dashDuration) : 1f, caster, ja.GetType().Name);
                    // Iteration 39 (run-047's death: a cone 593 at 9.1 m from the point, nothing listed): the five cones' whole reach
                    // round the point - their start angle is random until they exist - landing a cone's damageDelay after the leap.
                    // Once it has landed the cones themselves are listed (polygons, turned to where they hit).
                    JumpDisc(list, h, radius, ja.info.point, Mathf.Max(0f, left) + ConeDelay(caster),
                        ja.dashDuration > 0.01f ? Mathf.Clamp01(1f - left / ja.dashDuration) : 1f, caster, ConeType + " (predicted)");
                }
                catch (Exception) { }
            }

            // Iteration 39: the Rage phase's Atk (Ai_..._Rage_Atk) is two channels, each ending in a swipe (FirstSwipe, SecondSwipe:
            // InstantDamageInstances) where the Atk was cast, facing as it was. Each swipe's range there while it is coming;
            // left = until the channel(s) before it end + its damageDelay.
            var swipe1 = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Rage_Atk_FirstSwipe>();
            var swipe2 = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Rage_Atk_SecondSwipe>();
            foreach (var ra in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Rage_Atk>())
            {
                try
                {
                    if (ra == null || !ra.isActive || ra.firstAtkChannel == null || ra.secondAtkChannel == null) continue;
                    var pr = ra.info.caster;
                    if (pr == null || !Hostile(hero, pr)) continue;
                    float age = Time.time - ra.creationTime;
                    float d1 = ra.firstAtkChannel.duration, d2 = ra.secondAtkChannel.duration;
                    if (age < d1 && swipe1 != null && swipe1.range != null)
                        AddColliderStrike(list, h, radius, swipe1.range, ra.position, ra.rotation, d1 - age + IdiDelay(swipe1, pr),
                            d1 > 0.01f ? Mathf.Clamp01(age / d1) : 1f, pr, "Windup_" + swipe1.GetType().Name, swipe1.transform);
                    if (age < d1 + d2 && swipe2 != null && swipe2.range != null)
                        AddColliderStrike(list, h, radius, swipe2.range, ra.position, ra.rotation, d1 + d2 - age + IdiDelay(swipe2, pr),
                            d1 + d2 > 0.01f ? Mathf.Clamp01(age / (d1 + d2)) : 1f, pr, "Windup_" + swipe2.GetType().Name, swipe2.transform);
                }
                catch (Exception) { }
            }

            // Iteration 42 (run-050's death in the Adapt phase: 1146 from its Adapt Atk, nothing listed): the Adapt Atk's bolt in
            // flight (Ai_..._Adapt_Atk: a StandardProjectile). On landing it hits the best entity within groundHitChainRadius of its
            // point (entityHitChainRadius when it was aimed at an entity), then 0.3 s later sends the next link: at that entity
            // (homing - the chain on the hero, 15% hits, not to be dodged) or, if it hit no one, at a point 2 m farther from Primus
            // (+- 1.5 m), up to totalHits in all. A bolt flying at a point: a strike there (left = its flight left) and the next
            // links predicted outward from Primus. Homing links are left to the projectile list.
            foreach (var aa in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk>())
            {
                try
                {
                    if (aa == null || !aa.isActive || aa.isCompleted || aa.mode == Projectile.ProjectileMode.Target) continue;
                    var pr = aa.info.caster;
                    if (pr == null || pr.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, pr)) continue;
                    var land = aa.Network_targetPosition;
                    float speed = Mathf.Max(aa.targetSpeed, aa.initialSpeed, 0.1f);
                    int done = AdaptDoneField != null ? (int)AdaptDoneField.GetValue(aa) : 0;
                    AdaptLanding(list, h, radius, pr, aa, land, aa.info.target != null, Flat(land - aa.position).magnitude / speed,
                        Mathf.Clamp01(aa.normalizedPosition), aa.totalHits - done - 1, aa.GetType().Name);
                }
                catch (Exception) { }
            }

            // Iteration 42: the Arbalest (Adapt < 80%): Primus stands (everything blocked) and shoots shootCount bolts, each after
            // aiming aimTime (1.5 s) - the aim turns after where the hero will be (with a random lead of 0.5-1) every frame - then
            // afterShootDelay. The bolt flies from Primus along the aim. The aim line as a box from Primus along its current aim,
            // the bolt's reach long and its collision radius to each side; left = until the next bolt reaches the hero's point on it.
            var bolt = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Adapt_Arbalest_Projectile>();
            foreach (var ab in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Adapt_Arbalest>())
            {
                try
                {
                    if (bolt == null || ab == null || !ab.isActive) continue;
                    var pr = ab.info.caster;
                    if (pr == null || pr.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, pr)) continue;
                    float cycle = Mathf.Max(0.05f, ab.aimTime + ab.afterShootDelay);
                    float age = Time.time - ab.creationTime;
                    int i = Mathf.FloorToInt(age / cycle);
                    float inCycle = age - i * cycle;
                    bool aiming = inCycle < ab.aimTime;
                    if (i >= ab.shootCount || (!aiming && i + 1 >= ab.shootCount)) continue;
                    float wait = aiming ? ab.aimTime - inCycle : cycle - inCycle + ab.aimTime;
                    var fwd = Flat(ab.rotation * Vector3.forward).normalized;
                    float speed = Mathf.Max(bolt.targetSpeed, bolt.initialSpeed, 0.1f);
                    AddLine(list, h, radius, pr.agentPosition + fwd * bolt.startInFrontDistance, fwd, bolt.endDistance, Mathf.Max(0.3f, bolt.collisionRadius),
                        wait, speed, aiming ? Mathf.Clamp01(inCycle / Mathf.Max(0.05f, ab.aimTime)) : 0f, pr, ab.GetType().Name + " (aim)");
                }
                catch (Exception) { }
            }

            // Primus's entrance: a blow in its `range` (3 m in front of where it appears) after spawnDuration.
            foreach (var sa in Describe.Actors<Ai_Mon_Primus_SpawnAttack>())
            {
                try
                {
                    if (sa == null || !sa.isActive || sa.range == null) continue;
                    var caster = sa.info.caster;
                    if (caster != null && !Hostile(hero, caster)) continue;
                    float left = sa.spawnDuration - (Time.time - sa.creationTime);
                    if (left < -0.1f) continue;
                    var t = sa.range.transform;
                    AddColliderStrike(list, h, radius, sa.range, t.position, t.rotation, Mathf.Max(0f, left),
                        sa.spawnDuration > 0.01f ? Mathf.Clamp01(1f - left / sa.spawnDuration) : 1f, caster, sa.GetType().Name);
                }
                catch (Exception) { }
            }

            // The Ice Block (Adapt): a shield of hpRatio of its max health for `duration`; when it breaks or runs out, a blow
            // round Primus (IceBlock_Damage). A strike on Primus, left = the time the block has left (it comes sooner if the
            // shield is broken).
            var iceBlow = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Adapt_IceBlock_Damage>();
            foreach (var ib in Describe.Actors<Se_Mon_Primus_BossPrimusAeron_Adapt_IceBlock>())
            {
                try
                {
                    if (iceBlow == null || iceBlow.range == null || ib == null || !ib.isActive) continue;
                    var pr = ib.victim;
                    if (pr == null || pr.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, pr)) continue;
                    float left = ib.remainingDuration ?? ib.duration;
                    AddColliderStrike(list, h, radius, iceBlow.range, pr.agentPosition, Quaternion.identity, Mathf.Max(0f, left),
                        ib.duration > 0.01f ? Mathf.Clamp01(1f - left / ib.duration) : 0f, pr, ib.GetType().Name);
                }
                catch (Exception) { }
            }

            // The Smite Storm (Adapt < 75%): Primus dashes to the arena's centre, and after 1.5 s sends waveCount waves of smites
            // out along four rays (wave w at w x 45 deg, then every 90 deg; smite i at 3.5 + 3.5 i m, one step every 0.1 s, each
            // landing its damageDelay later). The smites not yet out, landing within 3 s, as strikes where they will be (the
            // ones out already are InstantDamageInstances above).
            var smite = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Adapt_SmiteStorm_Smite>();
            foreach (var ss in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Adapt_SmiteStorm>())
            {
                try
                {
                    if (smite == null || smite.range == null || ss == null || !ss.isActive) continue;
                    var pr = ss.info.caster;
                    if (pr == null || !Hostile(hero, pr)) continue;
                    var arena = SingletonBehaviour<Room_BossArena>.instance;
                    float age = Time.time - ss.creationTime;
                    var o = age >= 1.5f || arena == null ? pr.agentPosition : arena.center;
                    float waveLen = ss.stormCount * 0.1f + 0.1f;
                    for (int w = 0; w < ss.waveCount; w++)
                    {
                        if (w >= 4) break;   // later waves turn at random
                        for (int i = 0; i < ss.stormCount; i++)
                        {
                            float born = 1.5f + w * waveLen + i * 0.1f;
                            float left = born + smite.damageDelay - age;
                            if (born <= age || left > 3f) continue;
                            for (int k = 0; k < 4; k++)
                            {
                                var at = o + Quaternion.Euler(0f, w * 45f + k * 90f, 0f) * Vector3.forward * (3.5f + i * 3.5f);
                                AddColliderStrike(list, h, radius, smite.range, at, Quaternion.identity, left,
                                    Mathf.Clamp01(1f - left / 3f), pr, smite.GetType().Name + " (predicted)");
                            }
                        }
                    }
                }
                catch (Exception) { }
            }

            // The phase change's blast (Se_Mon_Primus_BossPrimusAeron_PhaseSwitcher): the blow that brings Primus to 0 in Force
            // or Adapt sets it to 1 hp and, that same moment, knocks back and stuns (explodeStunDuration) everyone in
            // explodeRange round it - no damage, no telegraph. Listed while Primus can still change phase: shape "blast" - an
            // explosion armed to go off at the killing blow; fill = how far down its health is (1 - hp / max), left 99 (no time).
            foreach (var ps in Describe.Actors<Se_Mon_Primus_BossPrimusAeron_PhaseSwitcher>())
            {
                try
                {
                    if (ps == null || !ps.isActive || ps.explodeRange == null) continue;
                    var pr = ps.victim;
                    if (pr == null || pr.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, pr)) continue;
                    if (pr.phase != Mon_Primus_BossPrimusAeron.PhaseType.Force && pr.phase != Mon_Primus_BossPrimusAeron.PhaseType.Adapt) continue;
                    var col = ps.explodeRange;
                    var c = pr.agentPosition;
                    float rad;
                    if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * col.transform.lossyScale.x;
                    else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * col.transform.lossyScale.x, col.size.y * col.transform.lossyScale.z);
                    else continue;
                    float d = Flat(h - c).magnitude;
                    if (rad <= 0.05f || d > radius + rad) continue;
                    list.Add(new Area
                    {
                        shape = "blast",
                        by = Describe.EntityName(pr),
                        centre = Describe.Vec(c),
                        radius = R(rad),
                        angle = 360f,
                        fill = R(Mathf.Clamp01(1f - pr.normalizedHealth)),
                        left = 99f,
                        inside = d <= rad,
                        edge = R(Mathf.Max(0f, rad - d)),
                        type = ps.GetType().Name,
                    });
                }
                catch (Exception) { }
            }
        }

        // A straight sweep: from o along fwd for `length`, `half` m to each side. left = wait + seconds until the front
        // (moving at `speed` from o; 0 = all at once) reaches the hero's point along it. Listed as a "box".
        private static void AddLine(List<Area> list, Vector3 h, float radius, Vector3 o, Vector3 fwd, float length, float half,
            float wait, float speed, float fill, Entity caster, string type)
        {
            if (fwd.sqrMagnitude < 0.01f || length <= 0.1f) return;
            var rel = Flat(h - o);
            float along = Vector3.Dot(rel, fwd);
            var across = new Vector3(fwd.z, 0f, -fwd.x);
            float off = Mathf.Abs(Vector3.Dot(rel, across));
            if (Flat(o - h).magnitude > radius + length) return;
            var end = o + fwd * length;
            var world = new[] { o - across * half, o + across * half, end + across * half, end - across * half };
            bool inside = along >= 0f && along <= length && off <= half;
            float left = wait + (speed > 0.01f ? Mathf.Max(0f, along) / speed : 0f);
            list.Add(new Area
            {
                shape = "box",
                by = caster != null ? Describe.EntityName(caster) : null,
                centre = Describe.Vec((o + end) * 0.5f),
                corners = world.Select(v => Describe.Vec(v)).ToArray(),
                facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                fill = R(fill),
                left = R(Mathf.Max(0f, left)),
                inside = inside,
                edge = R(inside ? half - off : 0f),
                type = type,
            });
        }

        // A circle of radius r at c, as a strike.
        private static void AddCircle(List<Area> list, Vector3 h, float radius, Vector3 c, float r, float left, float fill, Entity caster, string type)
        {
            float d = Flat(h - c).magnitude;
            if (r <= 0.05f || d > radius + r) return;
            list.Add(new Area
            {
                shape = "strike",
                by = caster != null ? Describe.EntityName(caster) : null,
                centre = Describe.Vec(c),
                radius = R(r),
                angle = 360f,
                fill = R(fill),
                left = R(left),
                inside = d <= r,
                edge = R(Mathf.Max(0f, r - d)),
                type = type,
            });
        }

        // A collider where it is (its own transform), whatever its shape: a circle as a strike, a box as a "box", a
        // polygon as a "poly" (corners = its outline on the ground; centre and radius = its centroid and the farthest corner).
        private static void AddRange(List<Area> list, Vector3 h, float radius, DewCollider col, float left, float fill, Entity caster, string type)
        {
            var t = col.transform;
            var sc = t.lossyScale;
            var yaw = Quaternion.Euler(0f, t.rotation.eulerAngles.y, 0f);
            if (col.shape == DewCollider.ColliderShape.Circle)
            {
                AddCircle(list, h, radius, t.position + yaw * new Vector3(col.offset.x, 0f, col.offset.y), col.radius * sc.x, left, fill, caster, type);
                return;
            }
            Vector3[] world;
            if (col.shape == DewCollider.ColliderShape.Box)
            {
                float hx = col.size.x * 0.5f, hz = col.size.y * 0.5f;
                world = new[] { new Vector2(-hx, hz), new Vector2(hx, hz), new Vector2(hx, -hz), new Vector2(-hx, -hz) }
                    .Select(p => t.position + yaw * new Vector3((p.x + col.offset.x) * sc.x, 0f, (p.y + col.offset.y) * sc.z)).ToArray();
            }
            else if (col.points != null && col.points.Length >= 3)
                world = col.points.Select(p => t.position + yaw * new Vector3(p.x * sc.x, 0f, p.y * sc.z)).ToArray();
            else return;
            var c = world.Aggregate(Vector3.zero, (s, v) => s + v) / world.Length;
            float far = world.Max(v => Flat(v - c).magnitude);
            if (Flat(c - h).magnitude > radius + far) return;
            bool inside = InPolygon(world, h);
            float edge = inside ? EdgeDistance(world, h) : 0f;
            var fwd = Flat(t.forward).normalized;
            list.Add(new Area
            {
                shape = col.shape == DewCollider.ColliderShape.Box ? "box" : "poly",
                by = caster != null ? Describe.EntityName(caster) : null,
                centre = Describe.Vec(c),
                radius = R(far),
                corners = world.Select(v => Describe.Vec(v)).ToArray(),
                facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                fill = R(fill),
                left = R(left),
                inside = inside,
                edge = R(edge),
                type = type,
            });
        }

        // The largest circle centred on a collider's middle that stays inside it: a circle as it is, a box's shorter
        // half-side, a polygon's nearest edge from its centroid.
        private static bool InsideCircle(DewCollider col, out Vector3 c, out float rad)
        {
            var t = col.transform;
            var sc = t.lossyScale;
            var yaw = Quaternion.Euler(0f, t.rotation.eulerAngles.y, 0f);
            c = t.position; rad = 0f;
            switch (col.shape)
            {
                case DewCollider.ColliderShape.Circle:
                    c = t.position + yaw * new Vector3(col.offset.x * sc.x, 0f, col.offset.y * sc.z);
                    rad = col.radius * sc.x;
                    return true;
                case DewCollider.ColliderShape.Box:
                    c = t.position + yaw * new Vector3(col.offset.x * sc.x, 0f, col.offset.y * sc.z);
                    rad = 0.5f * Mathf.Min(col.size.x * sc.x, col.size.y * sc.z);
                    return true;
                default:
                    if (col.points == null || col.points.Length < 3) return false;
                    var world = col.points.Select(p => t.position + yaw * new Vector3(p.x * sc.x, 0f, p.y * sc.z)).ToArray();
                    c = world.Aggregate(Vector3.zero, (s, v) => s + v) / world.Length;
                    rad = InPolygon(world, c) ? EdgeDistance(world, c) : 0f;
                    return rad > 0f;
            }
        }

        private static bool InPolygon(Vector3[] poly, Vector3 p)
        {
            bool inside = false;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                if ((poly[i].z > p.z) != (poly[j].z > p.z) &&
                    p.x < (poly[j].x - poly[i].x) * (p.z - poly[i].z) / (poly[j].z - poly[i].z) + poly[i].x)
                    inside = !inside;
            }
            return inside;
        }

        private static float EdgeDistance(Vector3[] poly, Vector3 p)
        {
            float best = float.PositiveInfinity;
            for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
            {
                var a = Flat(poly[j]); var b = Flat(poly[i]); var q = Flat(p);
                var ab = b - a;
                float t = ab.sqrMagnitude > 1e-6f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / ab.sqrMagnitude) : 0f;
                best = Mathf.Min(best, (a + ab * t - q).magnitude);
            }
            return best;
        }

        // The collider a projectile hurts with where it lands: a DewCollider field named `range` on its type (null when it
        // has none). Cached by type. Left out: those whose `range` hurts along the way, not at the end.
        private static readonly Dictionary<Type, FieldInfo> LandingRangeField = new Dictionary<Type, FieldInfo>();
        private static readonly HashSet<string> RangeAlongTheWay = new HashSet<string> { "Ai_Mon_Ink_BossDarkMoon_ThrowSpear_TeleportAtk_AfterAtk" };
        private static DewCollider LandingRange(Projectile p)
        {
            var type = p.GetType();
            if (!LandingRangeField.TryGetValue(type, out var f))
            {
                f = RangeAlongTheWay.Contains(type.Name) ? null : AccessTools.Field(type, "range");
                if (f != null && !typeof(DewCollider).IsAssignableFrom(f.FieldType)) f = null;
                LandingRangeField[type] = f;
            }
            return f?.GetValue(p) as DewCollider;
        }

        private static Entity CasterOf(Component c)
        {
            try
            {
                var fx = c.GetComponentInParent<FxCastTelegraph>(true);
                return fx != null ? fx.castInfo.caster : null;
            }
            catch (Exception) { return null; }
        }

        private static float R(float v) => (float)Math.Round(v, 2);

        // When each Dark Moon Blade's channel began (the first look with her dash over), by instance.
        private static readonly Dictionary<uint, float> BladeStart = new Dictionary<uint, float>();
        // When each Death From Above's follow began (the first look with _followPos set), by instance.
        private static readonly Dictionary<uint, float> FallStart = new Dictionary<uint, float>();

        // A blow's collider placed at pos/rot, listed as a strike: its centre, its radius (a box as
        // a circle of half its longer side), the hero's depth in it.
        // Iteration 39: a polygon (Primus's) as a "poly" placed at pos/rot; root = the prefab the collider sits in (its place there).
        private static void AddColliderStrike(List<Area> list, Vector3 h, float radius, DewCollider col, Vector3 pos, Quaternion rot,
            float left, float fill, Entity caster, string type, Transform root = null)
        {
            if (col.shape == DewCollider.ColliderShape.Polygon)
            {
                if (!PolyCaster(caster)) return;
                var poly = PolyAt(col, root, pos, rot);
                if (poly != null) AddPoly(list, h, radius, poly, Flat(rot * Vector3.forward).normalized, left, fill, caster, type);
                return;
            }
            var c = pos + rot * new Vector3(col.offset.x, 0f, col.offset.y);
            var scale = col.transform.lossyScale;
            float rad;
            if (col.shape == DewCollider.ColliderShape.Circle) rad = col.radius * scale.x;
            else if (col.shape == DewCollider.ColliderShape.Box) rad = 0.5f * Mathf.Max(col.size.x * scale.x, col.size.y * scale.z);
            else return;
            float d = Flat(h - c).magnitude;
            if (rad <= 0.05f || d > radius + rad) return;
            list.Add(new Area
            {
                shape = col.shape == DewCollider.ColliderShape.Circle ? "strike" : "strikebox",
                by = caster != null ? Describe.EntityName(caster) : null,
                centre = Describe.Vec(c),
                radius = R(rad),
                angle = 360f,
                fill = R(fill),
                left = R(left),
                inside = d <= rad,
                edge = R(Mathf.Max(0f, rad - d)),
                type = type,
            });
        }

        // ----- iteration 39: Primus's polygon blows ---------------------------------------------------------------------
        // run-047 (the first run at Primus): its Force Atk (519, 4 m from it) and a Jump Attack cone (593, 9.1 m from the landing)
        // were never listed - both have a polygon collider, which every reader above skipped (circles and boxes only).

        // Whose polygons are listed: Primus's (its great sword's Atk and Swipe, the Jump Attack's cones, Rage's swipes). Other
        // casters' polygon blows stay unlisted as before - listing them changes every zone's fights (a later step).
        // Iteration 51: and Dark Moon's (her TeleportAtk, her rage Hammer) and Infernus's - its Atk, a wedge of 7.42 m from its
        // centre (the bundle), never listed, hit at 5.6-7.15 m (runs 042-053, 64-128 each + the burn it lights) while the fight
        // held 7.35 m; now from its wind-up (the boss reader below) and while its instance waits.
        // Iteration 54: and the Seeker's (its TunnelVision Claw: a 5 m claw polygon, an InstantDamageInstance - 99 in run-035, the one
        // zone-boss polygon blow besides those above that hit in runs 030-056; the others' cost 0 there).
        private static bool PolyCaster(Entity caster) => caster is Mon_Primus_BossPrimusAeron || caster is Mon_Ink_BossDarkMoon || caster is Mon_LavaLand_BossInfernus
                                                         || caster is Mon_DarkCave_BossSeeker;

        private static readonly FieldInfo ConeAngleField = AccessTools.Field(typeof(Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_ConeInstance), "_originalAngle");
        private const string ConeType = "Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_ConeInstance";
        // Where each Jump Attack cast (by trigger) is aimed: the hero's place when its wind-up was first seen.
        private static readonly Dictionary<uint, Vector3> JumpCast = new Dictionary<uint, Vector3>();

        // A live polygon collider's outline on the ground. A Jump Attack cone turns 90 deg (eased) over its damageDelay and hits
        // where it ends up (Ai_..._JumpAttack_ConeInstance.ActiveFrameUpdate): its outline turned the rest of the way round the
        // cone's origin (_originalAngle + 90, a pure read).
        private static Vector3[] PolyNow(InstantDamageInstance di, DewCollider col)
        {
            if (col.points == null || col.points.Length < 3) return null;
            var t = col.transform;
            var sc = t.lossyScale;
            var yaw = Quaternion.Euler(0f, t.rotation.eulerAngles.y, 0f);
            var world = col.points.Select(p => t.position + yaw * new Vector3(p.x * sc.x, 0f, p.y * sc.z)).ToArray();
            if (di is Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_ConeInstance && ConeAngleField != null)
            {
                float end = (float)ConeAngleField.GetValue(di) + 90f;
                var turn = Quaternion.Euler(0f, Mathf.DeltaAngle(di.transform.eulerAngles.y, end), 0f);
                var o = di.transform.position;
                world = world.Select(v => o + turn * (v - o)).ToArray();
            }
            return world;
        }

        // A polygon collider's outline were its instance (root: the prefab it sits in; null: the collider itself) placed at pos,
        // turned as rot. The same mapping as DewCollider's proxy: points (x, y) -> (x, z), scaled, turned by the yaw.
        private static Vector3[] PolyAt(DewCollider col, Transform root, Vector3 pos, Quaternion rot)
        {
            if (col.points == null || col.points.Length < 3) return null;
            var t = col.transform;
            var sc = t.lossyScale;
            var yaw = Quaternion.Euler(0f, rot.eulerAngles.y, 0f);
            var rel = Vector3.zero;
            float relYaw = 0f;
            if (root != null && t != root)
            {
                rel = Quaternion.Euler(0f, -root.eulerAngles.y, 0f) * Flat(t.position - root.position);
                relYaw = t.eulerAngles.y - root.eulerAngles.y;
            }
            var o = pos + yaw * rel;
            var q = yaw * Quaternion.Euler(0f, relYaw, 0f);
            return col.points.Select(p => o + q * new Vector3(p.x * sc.x, 0f, p.y * sc.z)).ToArray();
        }

        // An outline as a "poly": corners, centre = its centroid, radius = its farthest corner from that, facing, the hero's
        // depth in it (edge = the way to its nearest edge).
        private static void AddPoly(List<Area> list, Vector3 h, float radius, Vector3[] world, Vector3 fwd, float left, float fill, Entity caster, string type)
        {
            var c = world.Aggregate(Vector3.zero, (s, v) => s + v) / world.Length;
            float far = world.Max(v => Flat(v - c).magnitude);
            if (Flat(c - h).magnitude > radius + far) return;
            bool inside = InPolygon(world, h);
            list.Add(new Area
            {
                shape = "poly",
                by = caster != null ? Describe.EntityName(caster) : null,
                centre = Describe.Vec(c),
                radius = R(far),
                corners = world.Select(v => Describe.Vec(v)).ToArray(),
                facing = new { x = Math.Round(fwd.x, 3), z = Math.Round(fwd.z, 3) },
                fill = R(fill),
                left = R(Mathf.Max(0f, left)),
                inside = inside,
                edge = R(inside ? EdgeDistance(world, h) : 0f),
                type = type,
            });
        }

        // An InstantDamageInstance's delay before it hits (shorter with its caster's attack speed when it says so).
        private static float IdiDelay(InstantDamageInstance di, Entity caster) =>
            di.affectedByAttackSpeed && caster != null && !caster.IsNullInactiveDeadOrKnockedOut()
                ? di.damageDelay / Mathf.Max(0.01f, caster.Status.attackSpeedMultiplier) : di.damageDelay;

        // A Jump Attack cone's delay from the landing to its hit.
        private static float ConeDelay(Entity caster)
        {
            var cone = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_ConeInstance>();
            return cone != null ? IdiDelay(cone, caster) : 0.75f;
        }

        // The Jump Attack's landing before it has landed: a strike round `at` as far as its cones reach from their origin (their
        // start angle is random) or its circle, whichever is larger.
        private static void JumpDisc(List<Area> list, Vector3 h, float radius, Vector3 at, float left, float fill, Entity caster, string type)
        {
            var cone = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_ConeInstance>();
            var circle = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Force_JumpAttack_CircleInstance>();
            float r = 0f;
            if (cone != null && cone.range != null)
            {
                var cc = cone.range;
                float s = cc.transform.lossyScale.x;
                if (cc.shape == DewCollider.ColliderShape.Polygon)
                {
                    var poly = PolyAt(cc, cone.transform, Vector3.zero, Quaternion.identity);
                    if (poly != null) r = poly.Max(v => Flat(v).magnitude);
                }
                else if (cc.shape == DewCollider.ColliderShape.Circle) r = (cc.offset.magnitude + cc.radius) * s;
                else r = new Vector2(Mathf.Abs(cc.offset.x) + cc.size.x * 0.5f, Mathf.Abs(cc.offset.y) + cc.size.y * 0.5f).magnitude * s;
            }
            if (circle != null && circle.range != null && circle.range.shape == DewCollider.ColliderShape.Circle)
                r = Mathf.Max(r, circle.range.radius * circle.range.transform.lossyScale.x);
            if (r <= 0.05f) return;
            AddCircle(list, h, radius, at, r, left, fill, caster, type);
        }

        // ----- iteration 46: Primus's Doom --------------------------------------------------------------------------------
        // run-052 died in Doom to its meteors' fireballs: 315, 299 and a last 111 (the hero's last hp) - ~300 each, a quarter of
        // the hero's health, with nothing on the ground (run-051's "7.9" was the 8 hp it had left). Se_..._Adapt_Doom_Ongoing drops
        // 4 x 2 meteors (Ai_..._Adapt_Doom_Meteor, an InstantDamageInstance: its range, a stun) at random pathable places; each,
        // when it lands, sends 4 rings 0.175 s apart of RoundToInt(subMeteorCount x Lerp(0.7, 1, the difficulty's
        // specialSkillChanceMultiplier)) fireballs (Ai_..._Doom_Meteor_SubFireball, a StandardProjectile) from its centre - fireball
        // i of ring w at the world yaw 360 / n x (i + w / 2), rings 1 and 3 on the same spokes, 2 and 4 half a step turned - out
        // to the prefab's endDistance at its speed. Fixed spokes from each centre, known before the meteor lands (run-052: 18 a
        // ring, ~5 m/s, all three hits within 0.45 m of a spoke 16-22 m out). `doom`: each meteor from its creation until its
        // last ring is past its reach - centre, left (to its landing; below 0 after it), n (fireballs a ring), rings, ringGap (s),
        // step (the spokes' degrees, 360 / n; the other set turned `half`), speed, reach and radius (a fireball's collision
        // radius), strike (the meteor's own radius) and stun. Read-only: the instances' fields, the prefab, the clock.
        private const int DoomRingCount = 4;
        private const float DoomRingGap = 0.175f;
        private sealed class DoomMeteor { public Vector3 at; public float landAt; public int n; public float strike; public float stun; }
        private static readonly Dictionary<string, DoomMeteor> DoomSeen = new Dictionary<string, DoomMeteor>();

        private static List<object> DoomMeteors(Hero hero, Vector3 h, float radius)
        {
            var list = new List<object>();
            try
            {
                float now = Time.time;
                var sub = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Adapt_Doom_Meteor_SubFireball>();
                float speed = sub != null ? Mathf.Max(sub.targetSpeed, sub.initialSpeed, 0.1f) : 5f;
                float reach = sub != null ? sub.endDistance : 8f;
                float cr = sub != null ? sub.collisionRadius : 0.25f;
                float mult = 1f;
                try { mult = NetworkedManagerBase<GameManager>.instance.difficulty.specialSkillChanceMultiplier; } catch (Exception) { }
                foreach (var m in Describe.Actors<Ai_Mon_Primus_BossPrimusAeron_Adapt_Doom_Meteor>())
                {
                    try
                    {
                        if (m == null || !m.isActive) continue;
                        var caster = m.info.caster;
                        if (caster == null || !Hostile(hero, caster)) continue;
                        // Pooled (reuseInRoom): the same actor comes back for a later meteor - keyed by its creation too.
                        string key = m.netId + ":" + m.creationTime.ToString("F2");
                        if (DoomSeen.ContainsKey(key)) continue;
                        float strike = 0f;
                        if (m.range != null && m.range.shape == DewCollider.ColliderShape.Circle) strike = m.range.radius * m.range.transform.lossyScale.x;
                        DoomSeen[key] = new DoomMeteor
                        {
                            at = m.position,
                            landAt = m.creationTime + IdiDelay(m, caster),
                            n = Mathf.Max(1, Mathf.RoundToInt(m.subMeteorCount * Mathf.Lerp(0.7f, 1f, mult))),
                            strike = strike,
                            stun = m.stunDuration,
                        };
                    }
                    catch (Exception) { }
                }
                float life = reach / speed + DoomRingCount * DoomRingGap + 0.5f;
                foreach (var k in DoomSeen.Where(kv => now - kv.Value.landAt > life || kv.Value.landAt - now > 30f).Select(kv => kv.Key).ToList()) DoomSeen.Remove(k);
                foreach (var dm in DoomSeen.Values.OrderBy(x => x.landAt))
                {
                    if (Flat(dm.at - h).magnitude > radius + reach) continue;
                    list.Add(new
                    {
                        centre = Describe.Vec(dm.at),
                        left = R(dm.landAt - now),
                        n = dm.n,
                        rings = DoomRingCount,
                        ringGap = DoomRingGap,
                        step = R(360f / dm.n),
                        half = R(180f / dm.n),
                        speed = R(speed),
                        reach = R(reach),
                        radius = R(cr),
                        strike = R(dm.strike),
                        stun = R(dm.stun),
                    });
                }
            }
            catch (Exception) { }
            return list;
        }

        // ----- iteration 42: Primus's Adapt and Rage phases ------------------------------------------------------------
        // run-050 (the first run into the Adapt phase: dead in 15 s, 1146 of it from the Adapt Atk): its drawing is a blob
        // (FxCastTelegraph, the trigger's effectOnCast) of 7.08 m centred where the hero stood - 2.36 x the 3 m it hits, as
        // Starfall's 3.54 m blobs are 2.36 x its 1.5 m strikes - and nothing timed was listed.

        private static readonly FieldInfo AdaptDoneField = AccessTools.Field(typeof(Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk), "_doneHits");
        private const float AdaptLinkStep = 2f, AdaptLinkSpread = 0.75f;

        // The Adapt Atk while its trigger channels: where it is aimed - the cast's drawing (the FxCastTelegraph of this caster:
        // the one made from the trigger's effectOnCast, else any Point cast's) gives the cast's point (or its target), else the
        // hero's place when the wind-up was first seen - clamped to the cast range as OnCastComplete does; left = the channel
        // left + the bolt's flight there (from Primus, at its speed).
        private static void AdaptAtkWindup(List<Area> list, Vector3 h, float radius, Entity boss, AbilityTrigger t, Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk prefab, float chLeft, float fill)
        {
            Vector3 at = Vector3.zero;
            bool found = false, targeted = false;
            try
            {
                var fxPrefab = t.currentConfig.effectOnCast;
                FxCastTelegraph pick = null;
                foreach (var fx in UnityEngine.Object.FindObjectsByType<FxCastTelegraph>(FindObjectsSortMode.None))
                {
                    if (fx == null || !fx.isActiveAndEnabled) continue;
                    Entity c = null;
                    try { c = fx.castInfo.caster; } catch (Exception) { }
                    if (c != boss) continue;
                    if (fxPrefab != null && fx.name.StartsWith(fxPrefab.name)) { pick = fx; break; }
                    if (pick == null && (fx.castMethod == CastMethodType.Point || fx.castMethod == CastMethodType.Target)) pick = fx;
                }
                if (pick != null)
                {
                    var ci = pick.castInfo;
                    targeted = ci.target != null;
                    at = pick.castMethod == CastMethodType.Target && ci.target != null ? ci.target.position : ci.point;
                    found = pick.castMethod == CastMethodType.Point || (pick.castMethod == CastMethodType.Target && ci.target != null);
                }
            }
            catch (Exception) { found = false; }
            if (!found)
            {
                if (!JumpCast.TryGetValue(t.netId, out at)) { at = h; JumpCast[t.netId] = at; }
            }
            try
            {
                float range = t.currentConfig.castMethod.pointData.range;
                var o = boss.agentPosition;
                var rel = Flat(at - o);
                if (range > 0.1f && rel.magnitude > range) at = o + rel.normalized * range;
            }
            catch (Exception) { }
            float speed = Mathf.Max(prefab.targetSpeed, prefab.initialSpeed, 0.1f);
            float flight = Mathf.Max(0f, Flat(at - boss.position).magnitude - prefab.startInFrontDistance) / speed;
            AdaptLanding(list, h, radius, boss, prefab, at, targeted, chLeft + flight, fill, prefab.totalHits - 1, "Windup_" + t.GetType().Name);
        }

        // An Adapt Atk bolt landing at `at` in `left` s: a strike of the radius it hits in there, and - should it hit no one - the
        // chain's next links (up to 2 of linksLeft) predicted AdaptLinkStep m farther from Primus each, a link's interval + flight
        // later, their radius widened by the link's random offset (AdaptLinkSpread of its 1.5 m), typed "<type> (next link)".
        private static void AdaptLanding(List<Area> list, Vector3 h, float radius, Entity caster, Ai_Mon_Primus_BossPrimusAeron_Adapt_Atk prefab,
            Vector3 at, bool targeted, float left, float fill, int linksLeft, string type)
        {
            float r = targeted ? prefab.entityHitChainRadius : prefab.groundHitChainRadius;
            AddCircle(list, h, radius, at, r, Mathf.Max(0f, left), fill, caster, type);
            var u = Flat(at - caster.agentPosition);
            if (u.magnitude < 0.1f) return;
            u.Normalize();
            float speed = Mathf.Max(prefab.targetSpeed, prefab.initialSpeed, 0.1f);
            for (int k = 1; k <= Mathf.Min(2, linksLeft); k++)
                AddCircle(list, h, radius, at + u * (AdaptLinkStep * k), prefab.groundHitChainRadius + AdaptLinkSpread,
                    Mathf.Max(0f, left) + k * (prefab.intervalBetweenChain + AdaptLinkStep / speed), 0f, caster, type + " (next link)");
        }

        // Rage's Atk placed at pos / rot (where the Rage_Atk instance will be): its first swipe `pre` + the first channel + its
        // damageDelay off, the second after the second channel too. The swipes are polygons (listed as `poly`) or circles.
        private static void RageSwipes(List<Area> list, Vector3 h, float radius, Entity pr, Vector3 pos, Quaternion rot, Ai_Mon_Primus_BossPrimusAeron_Rage_Atk ra,
            float pre, float fill, string type)
        {
            var s1 = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Rage_Atk_FirstSwipe>();
            var s2 = DewResources.GetByType<Ai_Mon_Primus_BossPrimusAeron_Rage_Atk_SecondSwipe>();
            float d1 = ra.firstAtkChannel != null ? ra.firstAtkChannel.duration : 0f, d2 = ra.secondAtkChannel != null ? ra.secondAtkChannel.duration : 0f;
            if (s1 != null && s1.range != null)
                AddColliderStrike(list, h, radius, s1.range, pos, rot, pre + d1 + IdiDelay(s1, pr), fill, pr, type + " (first swipe)", s1.transform);
            if (s2 != null && s2.range != null)
                AddColliderStrike(list, h, radius, s2.range, pos, rot, pre + d1 + d2 + IdiDelay(s2, pr), fill * 0.5f, pr, type + " (second swipe)", s2.transform);
        }

        // Primus's state for the bot: phase (Force, Adapt, Rage; InTransition while it changes), weapon, whether its armor is
        // broken, and its health and shield. null when there is no Primus.
        private static object PrimusState(Hero hero)
        {
            try
            {
                foreach (var pr in Describe.Actors<Mon_Primus_BossPrimusAeron>())
                {
                    if (pr == null || pr.IsNullInactiveDeadOrKnockedOut() || !Hostile(hero, pr)) continue;
                    return new
                    {
                        id = pr.netId,
                        phase = pr.phase.ToString(),
                        weapon = pr.weapon.ToString(),
                        armorBroken = pr.isArmorBroken,
                        hp = R(pr.currentHealth),
                        maxHp = R(pr.maxHealth),
                        shield = R(pr.Status.currentShield),
                    };
                }
            }
            catch (Exception) { }
            return null;
        }

        // Half the width of a blow's collider across its direction.
        private static float RangeHalfWidth(DewCollider col)
        {
            var scale = col.transform.lossyScale;
            if (col.shape == DewCollider.ColliderShape.Circle) return col.radius * scale.x;
            if (col.shape == DewCollider.ColliderShape.Box) return 0.5f * col.size.x * scale.x;
            return 1.5f;
        }

        private static readonly FieldInfo SafePointsField = AccessTools.Field(typeof(Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone), "_points");
        private static readonly FieldInfo SafeEndField = AccessTools.Field(typeof(Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone), "_endTime");
        private static readonly FieldInfo GroundOnField = AccessTools.Field(typeof(Ink_BossRoomDamageGround), "_spawnGroundEnable");

        private static List<Vector3> SafePoints(Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone sz) => SafePointsField?.GetValue(sz) as List<Vector3>;
        private static float SafeEnd(Ai_Mon_Ink_BossWhiteNight_Cataclysm_SafeZone sz) => SafeEndField != null ? (float)SafeEndField.GetValue(sz) : 0f;
        private static bool GroundOn(Ink_BossRoomDamageGround g) => GroundOnField == null || (bool)GroundOnField.GetValue(g);

        private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

        private sealed class Shot
        {
            public uint id;
            public string type;
            public object position;
            public object heading;
            public float speed;
            public float radius;
            public bool homing;
            public float miss;
            public float eta;
            public float remaining;
        }

        private static Shot Read(Projectile p, Hero hero, Vector3 h)
        {
            var target = p.Network_targetEntity;
            bool homing = p.mode == Projectile.ProjectileMode.Target && target == hero;
            var goal = p.mode == Projectile.ProjectileMode.Target && target != null ? target.position : p.Network_targetPosition;
            var toGoal = Flat(goal - p.position);
            float remaining = toGoal.magnitude;
            var dir = remaining > 0.01f ? toGoal / remaining : Flat(p.transform.forward).normalized;

            float speed = p is StandardProjectile sp ? Mathf.Max(sp.targetSpeed, sp.initialSpeed, 0.1f) : 10f;

            // Closest pass of the hero's position along the line it flies, no further than it goes.
            var toHero = Flat(h - p.position);
            float along = Mathf.Clamp(Vector3.Dot(toHero, dir), 0f, homing ? toHero.magnitude : remaining);
            float miss = homing ? 0f : (toHero - dir * along).magnitude;

            return new Shot
            {
                id = p.netId,
                type = p.GetType().Name,
                position = Describe.Vec(p.position),
                heading = new { x = Math.Round(dir.x, 3), z = Math.Round(dir.z, 3) },
                speed = (float)Math.Round(speed, 2),
                radius = (float)Math.Round(p.collisionRadius, 2),
                homing = homing,
                miss = (float)Math.Round(miss, 2),
                eta = (float)Math.Round(along / speed, 2),
                remaining = (float)Math.Round(remaining, 2),
            };
        }
    }
}
#endif
