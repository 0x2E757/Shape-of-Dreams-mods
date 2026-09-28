using System;
using System.Collections.Generic;
using System.Reflection;

namespace ControlledMerge
{
    // An essence's strength is cut once, not once per number.
    //
    // Most essences with several values that grow with quality do several things with them - Perfect
    // grants six stats, Twilight damages and heals - and each of those is cut once. Some multiply
    // two of them into one effect. Scorched fires maxCount fireballs of dmgFactor each; cut both and
    // a copy at 52.5% throws 2 fireballs of 8 where one alone throws 3 of 16 - a third of the
    // damage, not a half. For those, one of the two is left whole.
    //
    // The one left whole is the count, the reach, the chance or the rate; the one cut is the amount.
    // An amount takes the factor exactly, where a count is rounded (3 x 0.525 is 2, a cut of a
    // third) and a chance or a rate is bent by the curve it goes through.
    //
    //   Scorched   fireballs per cast x damage of each
    //   Blade      blades per hit x damage of each
    //   Insight    enemies hit x damage to each
    //   Thunder    the charge cap, which is bolts per cast, x damage of each bolt
    //   Ricochet   the chance to ricochet x the share of the hit it carries
    //   Spiral     fireballs per second x damage of each
    //   Glaciate, Stillness   radius x damage
    //   Snow       the barrier x damage of a snowball: snowballs come faster the larger the barrier
    //
    // Pairs that look alike and are left as they are: a step and its cap (Crucible, Night Sky,
    // Omega) are both cut, which leaves the number of steps where it was and the cap cut once.
    // Glacial Core turns *every* heal the hero takes into damage and Glass amplifies every heal of
    // its memory, not only their own; left whole, the heals of everything else would pass through
    // them uncut.
    //
    // Ricochet's chance is not a ScalingValue - it comes from quality through a curve of its own -
    // and is simply not in QualityChances.
    internal static class OneCut
    {
        // (the essence, the type holding the value - the essence or what it creates, the field)
        private static readonly (Type gem, Type holder, string field)[] Whole =
        {
            (typeof(Gem_R_Scorched), typeof(Gem_R_Scorched), "maxCount"),
            (typeof(Gem_R_Blade), typeof(Gem_R_Blade), "baseCount"),
            (typeof(Gem_E_Insight), typeof(Gem_E_Insight), "maxHitCount"),
            (typeof(Gem_E_Thunder), typeof(Gem_E_Thunder), "maxCharge"),
            (typeof(Gem_R_Spiral), typeof(Gem_R_Spiral), "shootSpeed"),
            (typeof(Gem_R_Glaciate), typeof(Ai_Gem_R_Glaciate), "scale"),
            (typeof(Gem_R_Stillness), typeof(Ai_Gem_R_Stillness), "scale"),
            (typeof(Gem_R_Snow), typeof(Ai_Gem_R_Snow_Projectile), "damage"),
        };

        // A value reaches the patches as a ScalingValue, a struct, not as the field it came from;
        // so what is kept whole is recognised by its content, as the firing limits are. The
        // prefabs' values are the ones every copy and every instance is made with.
        private static Dictionary<Type, List<ScalingValue>> _byGem;

        public static bool Keeps(Gem gem, ScalingValue value)
        {
            if (gem == null) return false;
            var map = _byGem ?? (_byGem = Build());
            if (!map.TryGetValue(gem.GetType(), out var kept)) return false;
            foreach (var k in kept)
                if (Diminishing.Same(k, value)) return true;
            return false;
        }

        public static void Forget() => _byGem = null;

        private static Dictionary<Type, List<ScalingValue>> Build()
        {
            var map = new Dictionary<Type, List<ScalingValue>>();
            foreach (var (gem, holder, name) in Whole)
            {
                var field = holder.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var prefab = DewResources.GetByType(holder);
                if (field == null || prefab == null) continue;

                if (!map.TryGetValue(gem, out var list)) map[gem] = list = new List<ScalingValue>();
                list.Add((ScalingValue)field.GetValue(prefab));
            }
            return map;
        }

        // For the audit: each value kept whole, and any other scaling value of the same essence or
        // of what it creates with the same content - which would be kept whole along with it.
        public static IEnumerable<string> Describe()
        {
            foreach (var (gem, holder, name) in Whole)
            {
                string line = (holder == gem ? gem.Name : holder.Name) + "." + name;
                var field = holder.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                var prefab = DewResources.GetByType(holder);
                if (field == null || prefab == null)
                {
                    yield return line + " NOT FOUND";
                    continue;
                }

                var kept = (ScalingValue)field.GetValue(prefab);
                foreach (var type in new[] { gem, holder })
                {
                    var other = DewResources.GetByType(type);
                    if (other == null) continue;
                    for (var t = type; t != null && t != typeof(object); t = t.BaseType)
                        foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                            if (f.FieldType == typeof(ScalingValue) && !(type == holder && f.Name == name) &&
                                Diminishing.Same((ScalingValue)f.GetValue(other), kept))
                                line += " SAME AS " + type.Name + "." + f.Name;
                }
                yield return line;
            }
        }
    }
}
