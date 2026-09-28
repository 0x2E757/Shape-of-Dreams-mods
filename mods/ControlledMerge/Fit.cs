using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ControlledMerge
{
    // Whether a copy actually does anything where it sits, by AreMyGemsCompatible's own verdict -
    // when that mod is loaded. A copy that can never fire in its memory (a second Essence of Lava
    // in a memory that deals no Fire damage) is not a copy anyone is getting anything out of, so it
    // is left out of the count, and does not weaken the ones that work.
    //
    // Reached by reflection, as DevTools reaches it: neither mod references the other, and with
    // AreMyGemsCompatible not loaded every copy counts, as before.
    //
    // **Which copy of that mod is asked matters.** Every reload of the mods loads each assembly
    // again beside the old ones, and an old copy still defines Verdict - and one of a mod that has
    // since been turned off still defines it too. So the assembly used is the last one loaded whose
    // AreMyGemsCompatibleMod.Live is set, and Live is looked at again on every question: turning
    // the mod off clears it without loading anything new.
    internal static class Fit
    {
        private const string Namespace = "AreMyGemsCompatible.";
        private const BindingFlags Static = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        // Compatibility.Dead, AreMyGemsCompatible's enum - compared by value, since the type is
        // that assembly's.
        private const int Dead = 1;

        private static MethodInfo _verdict;
        private static FieldInfo _live;
        private static int _seenAssemblies = -1;

        // A verdict can look through the room's modifiers, and a value is read many times a frame.
        // So an answer is kept for half a second per essence and memory - short enough that an
        // essence socketed beside it shows almost at once.
        private const float KeepFor = 0.5f;
        private static readonly Dictionary<(int, int), (float time, bool fires)> Recent =
            new Dictionary<(int, int), (float, bool)>();

        // Whether AreMyGemsCompatible is loaded and answering. Also what the wording follows:
        // "used in" rather than "equipped in" once copies that do nothing are not counted.
        public static bool Available
        {
            get
            {
                Find();
                if (_live == null) return false;
                try { return _live.GetValue(null) != null; }
                catch (Exception) { return false; }
            }
        }

        // False only when AreMyGemsCompatible says the essence can never fire in that memory.
        public static bool Fires(Gem gem, SkillTrigger skill)
        {
            if (gem == null || skill == null || !Available) return true;

            var key = (gem.GetInstanceID(), skill.GetInstanceID());
            float now = Time.unscaledTime;
            if (Recent.TryGetValue(key, out var kept) && now - kept.time < KeepFor) return kept.fires;

            bool fires = true;
            try { fires = Convert.ToInt32(_verdict.Invoke(null, new object[] { gem, skill })) != Dead; }
            catch (Exception) { }

            if (Recent.Count > 256) Recent.Clear();
            Recent[key] = (now, fires);
            return fires;
        }

        public static void Forget()
        {
            Recent.Clear();
            _verdict = null;
            _live = null;
            _seenAssemblies = -1;
        }

        private static void Find()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (assemblies.Length == _seenAssemblies && (_live == null || LiveSet(_live))) return;
            _seenAssemblies = assemblies.Length;
            _verdict = null;
            _live = null;
            Recent.Clear();

            for (int i = assemblies.Length - 1; i >= 0; i--)
            {
                try
                {
                    var mod = assemblies[i].GetType(Namespace + "AreMyGemsCompatibleMod", false);
                    var live = mod?.GetField("Live", Static);
                    if (live == null || !LiveSet(live)) continue;

                    var verdict = assemblies[i].GetType(Namespace + "Verdict", false)?
                        .GetMethod("For", Static, null, new[] { typeof(Gem), typeof(SkillTrigger) }, null);
                    if (verdict == null) continue;

                    _verdict = verdict;
                    _live = live;
                    return;
                }
                catch (Exception) { }
            }
        }

        private static bool LiveSet(FieldInfo live)
        {
            try { return live.GetValue(null) != null; }
            catch (Exception) { return false; }
        }
    }
}
