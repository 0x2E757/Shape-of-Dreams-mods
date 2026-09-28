#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace DevTools
{
    // Why a healing shrine heals or does not: each step of Shrine_Guidance's burst as it happens, and every heal the
    // hero takes. Records only - no patch here changes an argument, a result or the flow.
    internal static class HealTrace
    {
        private const int Cap = 200;
        private static readonly List<object> Events = new List<object>();
        private static int _seq;

        internal static void Note(string what, object data)
        {
            lock (Events)
            {
                Events.Add(new { seq = ++_seq, time = (float)Math.Round(Time.time, 2), what, data });
                if (Events.Count > Cap) Events.RemoveRange(0, Events.Count - Cap);
            }
        }

        [Route("GET", "/heals/trace", "Healing shrines' bursts step by step (OnUse, OnHealEntity) and every heal the hero takes (DoHeal: by what, the amount asked, hp before and after), newest last.",
               "limit=50")]
        private static object List(Args a)
        {
            int limit = a.Int("limit", 50);
            lock (Events)
            {
                var list = Events.Skip(Math.Max(0, Events.Count - limit)).ToList();
                return new { last = _seq, events = list };
            }
        }

        [Route("GET", "/shrine/range", "A Shrine of Guidance's range as its burst reads it: the entities range.GetEntities(includeUncollidable) returns right now, each with owner.isHumanPlayer; whether the shrine and its range are active and the range has its 2D proxy.",
               "id")]
        private static object Range(Args a)
        {
            var actor = GameAccess.RequireActor(a.Id("id"));
            if (!(actor is Shrine_Guidance s)) throw new DevException(actor.GetType().Name + " is not a Shrine_Guidance");
            var r = s.range;
            var proxy = r != null ? Traverse.Create(r).Field("_proxy").GetValue<Behaviour>() : null;
            var found = new List<object>();
            if (r != null && proxy != null)
            {
                var list = r.GetEntities(out var handle, new CollisionCheckSettings { includeUncollidable = true });
                foreach (var e in list)
                    found.Add(new { id = e.netId, type = e.GetType().Name, human = e.owner != null && e.owner.isHumanPlayer, active = e.isActive,
                                    distance = (float)Math.Round(Vector3.Distance(e.position, s.position), 2) });
                handle.Return();
            }
            return new
            {
                shrineActive = s.gameObject.activeInHierarchy, s.isActive, s.isAvailable, s.totalUseCount,
                rangeActive = r != null && r.isActiveAndEnabled, proxy = proxy != null, proxyEnabled = proxy != null && proxy.enabled,
                proxyAt = proxy != null ? Describe.Vec(new Vector3(proxy.transform.position.x, 0, proxy.transform.position.y)) : null,
                overrides = s.actionOverride != null ? s.actionOverride.Count : 0,
                entities = found,
            };
        }
    }

    [HarmonyPatch(typeof(Shrine_Guidance), "OnUse")]
    internal static class GuidanceUseTrace
    {
        private static void Postfix(Shrine_Guidance __instance, Entity entity, bool __result) =>
            HealTrace.Note("OnUse", new { shrine = __instance.netId, type = __instance.GetType().Name, by = entity != null ? entity.GetType().Name : null,
                                          result = __result, active = __instance.gameObject.activeInHierarchy, __instance.explodeDelay });
    }

    [HarmonyPatch(typeof(Shrine_Guidance), nameof(Shrine_Guidance.OnHealEntity))]
    internal static class GuidanceHealTrace
    {
        private static void Prefix(Shrine_Guidance __instance, Entity e) =>
            HealTrace.Note("OnHealEntity", new { shrine = __instance.netId, entity = e != null ? e.GetType().Name : null,
                                                 hp = e != null ? (float)Math.Round(e.currentHealth, 1) : -1 });
    }

    [HarmonyPatch(typeof(Actor), nameof(Actor.DoHeal))]
    internal static class HeroHealTrace
    {
        private static void Prefix(Entity target, out float __state) => __state = target != null ? target.currentHealth : -1f;

        private static void Postfix(Actor __instance, HealData heal, Entity target, float __state)
        {
            if (target == null || target != GameAccess.Hero) return;
            // The basic-attack heal (Se_Star_L_HealOnAttack) fires every hit; keep only the others and the big ones.
            if (__instance is Se_Star_L_HealOnAttack && target.currentHealth - __state < 20f) return;
            HealTrace.Note("DoHeal", new { by = __instance.GetType().Name, parent = __instance.parentActor != null ? __instance.parentActor.GetType().Name : null,
                                           asked = (float)Math.Round(heal.originalAmount, 1), hpBefore = (float)Math.Round(__state, 1),
                                           hpAfter = (float)Math.Round(target.currentHealth, 1) });
        }
    }
}
#endif
