#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // Every hit the hero takes: what dealt it (the ability instance or the entity), who is behind
    // it, how much, where it stood. For finding out what an agent is failing to dodge - a hit from
    // an ability with no telegraph and no projectile, burning ground, a boss's channelled area.
    internal static class DamageLog
    {
        private const int Cap = 400;
        private static readonly List<Hit> Hits = new List<Hit>();
        private static Hero _subscribed;
        private static Action<EventInfoDamage> _handler;
        private static int _seq;

        private sealed class Hit
        {
            public int seq;
            public float time;
            public string by;
            public string source;
            public string caster;
            public float amount;
            public float shielded;
            public string element;
            public bool overTime;
            public object from;
            public float distance;
            public object heroAt;
            public float hpAfter;
        }

        // Called every frame by the server: follow the hero, whichever one it is now.
        public static void Tick()
        {
            var hero = GameAccess.Hero;
            if (hero == _subscribed) return;
            Unsubscribe();
            if (hero == null) return;
            _handler = OnDamage;
            hero.EntityEvent_OnTakeDamage += _handler;
            _subscribed = hero;
        }

        public static void Unsubscribe()
        {
            try { if (_subscribed != null && _handler != null) _subscribed.EntityEvent_OnTakeDamage -= _handler; }
            catch (Exception) { }
            _subscribed = null;
        }

        private static void OnDamage(EventInfoDamage info)
        {
            try
            {
                var hero = _subscribed;
                var actor = info.actor;
                Entity caster = null;
                try { caster = actor is Entity e ? e : actor is AbilityInstance ai ? ai.info.caster : actor != null ? actor.firstEntity : null; }
                catch (Exception) { }
                var from = actor != null ? actor.position : hero.position;
                lock (Hits)
                {
                    Hits.Add(new Hit
                    {
                        seq = ++_seq,
                        time = (float)Math.Round(Time.time, 2),
                        by = actor != null ? actor.GetType().Name : null,
                        source = info.damage.type.ToString(),
                        caster = caster != null ? Describe.EntityName(caster) : null,
                        amount = (float)Math.Round(info.damage.amount, 1),
                        shielded = (float)Math.Round(info.negatedAmountByShield, 1),
                        element = info.damage.elemental?.ToString(),
                        overTime = info.damage.HasAttr(DamageAttribute.DamageOverTime),
                        from = Describe.Vec(from),
                        distance = (float)Math.Round(Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(hero.position.x, 0, hero.position.z)), 2),
                        heroAt = Describe.Vec(hero.position),
                        hpAfter = (float)Math.Round(hero.currentHealth, 1),
                    });
                    if (Hits.Count > Cap) Hits.RemoveRange(0, Hits.Count - Cap);
                }
            }
            catch (Exception) { }
        }

        [Route("GET", "/damage", "The hits the hero has taken, newest last: what dealt each (an ability instance or an entity), whose it was, how much, how far away it came from. since=<seq> for only the new ones.",
               "since=0, limit=50")]
        private static object List(Args a)
        {
            int since = a.Int("since", 0);
            int limit = a.Int("limit", 50);
            lock (Hits)
            {
                var list = Hits.Where(h => h.seq > since).ToList();
                if (list.Count > limit) list = list.Skip(list.Count - limit).ToList();
                return new { last = _seq, hits = list };
            }
        }
    }
}
#endif
