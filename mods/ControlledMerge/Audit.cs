#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace ControlledMerge
{
    // Debug builds only: which values of which essences the cut reaches, read off the prefabs at
    // load and written to the log. It is the list docs/controlledmerge.md quotes, and the check that
    // nothing surprising scales - a firing limit, or a value the essence's code never reads.
    //
    // Essences, and the instances whose names say they belong to one (Se_Gem_*, Ai_Gem_*, and the
    // rest of the Se_/Ai_ types an essence's own name appears in). An instance reached any other
    // way is cut all the same in play; it is only not listed here.
    internal static class Audit
    {
        public static void Log()
        {
            var database = DewResources.database;
            if (database == null || database.typeNameToType == null) return;

            var gemNames = new List<string>();
            var lines = new StringBuilder();
            int gems = 0, gemsScaling = 0, values = 0, limits = 0, instances = 0;

            foreach (var pair in database.typeNameToType)
            {
                var type = pair.Value;
                if (type == null || type.IsAbstract || !typeof(Gem).IsAssignableFrom(type)) continue;
                if (!database.typeToGuid.ContainsKey(type)) continue;

                var gem = DewResources.GetByType(type) as Gem;
                if (gem == null) continue;
                gems++;
                gemNames.Add(type.Name.Substring(4));

                var scaling = ScalingFields(gem).ToList();
                var firing = scaling.Where(f => f.Name == "cooldownTime" || f.Name == "rateLimitTime" || f.Name == "rateLimitCount").ToList();
                limits += firing.Count;
                scaling = scaling.Except(firing).ToList();
                if (scaling.Count > 0) gemsScaling++;
                values += scaling.Count;

                lines.Append("\n  ").Append(type.Name).Append(": ")
                     .Append(scaling.Count > 0 ? string.Join(", ", scaling.Select(f => f.Name)) : "-");
                if (firing.Count > 0)
                    lines.Append("  [scaling firing limits left alone: ").Append(string.Join(", ", firing.Select(f => f.Name))).Append("]");
            }

            foreach (var pair in database.typeNameToType)
            {
                var type = pair.Value;
                if (type == null || type.IsAbstract || !typeof(AbilityInstance).IsAssignableFrom(type)) continue;
                if (!database.typeToGuid.ContainsKey(type)) continue;
                if (!BelongsToAnEssence(type.Name, gemNames)) continue;

                var instance = DewResources.GetByType(type) as AbilityInstance;
                if (instance == null) continue;

                var scaling = ScalingFields(instance).Select(f => f.Name).ToList();
                if (scaling.Count == 0) continue;
                instances++;
                lines.Append("\n  ").Append(type.Name).Append(": ").Append(string.Join(", ", scaling));
            }

            lines.Append("\n  left whole (OneCut): ").Append(string.Join(", ", OneCut.Describe()));

            // The essences' own methods patched here - OwnState's transpilers find their targets by
            // what the code calls, so this is the check that they found them.
            var own = HarmonyLib.Harmony.GetAllPatchedMethods()
                .Where(m => m.DeclaringType != null && (m.DeclaringType.FullName.StartsWith("Gem_", StringComparison.Ordinal) || m.DeclaringType.FullName.StartsWith("Ai_", StringComparison.Ordinal)) &&
                            HarmonyLib.Harmony.GetPatchInfo(m).Owners.Any(o => o.Contains("controlledmerge")))
                .Select(m => m.DeclaringType.FullName.Replace("Gem_", "") + "." + m.Name)
                .OrderBy(n => n);
            lines.Append("\n  essences' own code patched: ").Append(string.Join(", ", own));

            Debug.Log("[ControlledMerge] audit: " + gems + " essences, " + gemsScaling + " with " + values +
                      " scaling values of their own, " + limits + " scaling firing limits left alone, " +
                      instances + " of their instances with scaling values" + lines);
        }

        // What a worn essence's code reads right now: each scaling value through Gem.GetValue, as
        // its own code calls it, beside the factor it is cut by. For DevTools' /reflect/call.
        public static string Values(Gem gem)
        {
            if (gem == null) return "no essence";
            var share = Diminishing.Of(gem);
            var parts = new List<string> { "factor=" + share.Factor.ToString("0.####") + " (" + share.Memories + " memories, " + share.Copies + " copies)" };
            for (var type = gem.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(ScalingValue)) continue;
                    var value = (ScalingValue)field.GetValue(gem);
                    parts.Add(field.Name + "=" + gem.GetValue(value).ToString("0.#####") + (Diminishing.Scales(value) ? "" : " (fixed)"));
                }
            }
            return string.Join("; ", parts);
        }

        private static IEnumerable<FieldInfo> ScalingFields(object prefab)
        {
            for (var type = prefab.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                     BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType != typeof(ScalingValue)) continue;
                    if (Diminishing.Scales((ScalingValue)field.GetValue(prefab))) yield return field;
                }
            }
        }

        private static bool BelongsToAnEssence(string name, List<string> gemNames)
        {
            if (!name.StartsWith("Se_", StringComparison.Ordinal) && !name.StartsWith("Ai_", StringComparison.Ordinal))
                return false;
            if (name.Contains("_Gem_")) return true;
            foreach (var gem in gemNames)
                if (name.EndsWith("_" + gem, StringComparison.Ordinal) || name.Contains("_" + gem + "_")) return true;
            return false;
        }
    }
}
#endif
