using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace DevTools
{
    // The handle on MoreGemSlots' arrangement numbers, reached by reflection rather than by a
    // project reference.
    //
    // Not laziness: the two are separate mods, and the loader enables them independently. A
    // compile-time reference would make DevTools fail to load whenever MoreGemSlots is switched
    // off, which is a poor trade for a tool whose other controls have nothing to do with essence
    // slots. This way the section simply says the mod is not loaded.
    //
    // **Which copy of MoreGemSlots is the live one is the whole difficulty here.** DewMod.Load
    // calls Assembly.Load(File.ReadAllBytes(...)), so every hot reload puts *another* copy of the
    // assembly into the process, and nothing ever takes the old ones out - .NET cannot unload an
    // assembly without unloading its whole domain. Walking AppDomain.GetAssemblies() and taking a
    // match therefore lands on a dead copy as often as not, and editing its statics does exactly
    // nothing: the live mod is reading its own.
    //
    // So the assembly is not guessed. The live mod has a ModBehaviour component in the scene, and
    // the old ones do not, because unloading destroys them - so asking the scene which
    // MoreGemSlots is running answers it outright.
    //
    // Everything below therefore addresses fields **by name** rather than holding FieldInfos: a
    // reload replaces the type, and a FieldInfo from the previous one throws when handed an
    // object of the new.
    internal static class GemTuning
    {
        private const string ModTypeName = "MoreGemSlots.MoreGemSlots";
        private const string ArrangementTypeName = "MoreGemSlots.GemArrangement";

        // How often to look again. Cheap enough for a dev panel, and it has to keep looking rather
        // than resolve once: the mod is reloaded whenever its dll is rebuilt, which during tuning
        // is often.
        private const float RecheckSeconds = 2f;

        private static Type _type;
        private static object _hud;
        private static object _summary;
        private static MethodInfo _invalidate;
        private static MethodInfo _reset;
        private static readonly Dictionary<string, FieldInfo> ByName = new Dictionary<string, FieldInfo>();
        private static string[] _names = Array.Empty<string>();
        private static float _nextCheck;

        public static bool Available => Resolve();

        public static IReadOnlyList<string> FieldNames => Resolve() ? _names : Array.Empty<string>();

        private static object Target(bool summary) => summary ? _summary : _hud;

        // ----- finding the live copy ------------------------------------------------

        private static bool Resolve()
        {
            if (_summary != null && Time.unscaledTime < _nextCheck) return true;
            _nextCheck = Time.unscaledTime + RecheckSeconds;

            var assembly = LiveAssembly();
            if (assembly == null) { Forget(); return false; }

            Type type;
            try { type = assembly.GetType(ArrangementTypeName, false); }
            catch { type = null; }
            if (type == null) { Forget(); return false; }

            // The same copy as last time: nothing to rebind.
            if (type == _type && _summary != null) return true;

            const BindingFlags Statics = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
            var hud = type.GetField("Hud", Statics)?.GetValue(null);
            var summary = type.GetField("Summary", Statics)?.GetValue(null);
            if (summary == null) { Forget(); return false; }

            _type = type;
            _hud = hud;
            _summary = summary;
            _invalidate = type.GetMethod("Invalidate", Statics);
            _reset = type.GetMethod("ResetToShipped", Statics);

            ByName.Clear();
            var names = new List<string>();
            foreach (var field in summary.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (field.FieldType != typeof(float)) continue;
                ByName[field.Name] = field;
                names.Add(field.Name);
            }
            _names = names.ToArray();

            Debug.Log($"[DevTools] bound to {ArrangementTypeName} in {assembly.GetName().Name} " +
                      $"({_names.Length} numbers)");
            return true;
        }

        // The mod that is actually running, asked of the scene rather than of the assembly list.
        // Unloading a mod destroys its ModBehaviour, so only the live copy has one.
        private static Assembly LiveAssembly()
        {
            // Unsorted: the order is nothing to this, and sorting by instance id is the slower
            // half of the deprecated call this replaces.
            foreach (var mod in UnityEngine.Object.FindObjectsByType<ModBehaviour>(FindObjectsSortMode.None))
            {
                if (mod == null) continue;

                var type = mod.GetType();
                if (type.FullName == ModTypeName) return type.Assembly;
            }
            return null;
        }

        private static void Forget()
        {
            _type = null;
            _hud = null;
            _summary = null;
            _invalidate = null;
            _reset = null;
            ByName.Clear();
            _names = Array.Empty<string>();
        }

        // ----- reading and writing --------------------------------------------------

        public static float Get(bool summary, string name)
        {
            if (!Resolve() || !ByName.TryGetValue(name, out var field)) return 0f;
            return (float)field.GetValue(Target(summary));
        }

        public static void Add(bool summary, string name, float delta)
        {
            if (!Resolve() || !ByName.TryGetValue(name, out var field)) return;

            var target = Target(summary);

            // Rounded to the step, so a long walk up and down does not leave 0.30000001 behind and
            // the numbers stay in a state worth pasting into the source.
            float value = Mathf.Round(((float)field.GetValue(target) + delta) * 1000f) / 1000f;
            field.SetValue(target, value);
            _invalidate?.Invoke(null, null);
        }

        // Back to what the mod ships with, asked of the mod rather than worked out here: the two
        // sets no longer ship with the same numbers, and only that side knows which is which.
        public static void Restore(bool summary)
        {
            if (!Resolve()) return;

            if (_reset != null) { _reset.Invoke(null, new object[] { summary }); return; }

            // An older MoreGemSlots without the method: a plain default-constructed set is the
            // best that can be done, and is right for the HUD at least.
            var target = Target(summary);
            var fresh = Activator.CreateInstance(target.GetType());
            foreach (var field in ByName.Values) field.SetValue(target, field.GetValue(fresh));
            _invalidate?.Invoke(null, null);
        }

        // The point of the whole panel: numbers that can be pasted back into the source. Written
        // as the field initialisers they are, so the paste is a replacement rather than a
        // transcription.
        public static string Dump(bool summary, string label)
        {
            if (!Resolve()) return "MoreGemSlots is not loaded";

            var target = Target(summary);
            var text = new StringBuilder();
            text.Append("[DevTools] GemArrangement.").Append(label).AppendLine(" tuning:");
            foreach (var name in _names)
            {
                text.Append("            public float ").Append(name).Append(" = ")
                    .Append(((float)ByName[name].GetValue(target)).ToString("0.###")).AppendLine("f;");
            }

            Debug.Log(text.ToString());
            return "written to the player log";
        }
    }
}
