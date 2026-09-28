#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DevTools
{
    // Reads, writes and calls anything in the process by a path, for everything the semantic
    // routes do not cover. A path is a root followed by members:
    //
    //   ZoneManager.instance.currentNodeIndex        a type, then static and instance members
    //   $hero.Skill.gems                             $hero, $player, $N for a handle, #id for an actor
    //   $12.Status.statusEffects[0].remainingDuration
    //   DewSave.profileMain.heroes["Hero_Lacerta"]   indexers take numbers or strings
    //   $hero.GetComponent(HeroSkill)                a call with no arguments, or with type names
    //
    // Type names may be short (ZoneManager) or full (UnityEngine.Time). **When a mod has been hot
    // reloaded, several copies of its assembly are loaded at once** - .NET cannot unload one - and
    // only the last is live, so a short name resolves to the last assembly that has it.
    internal static class Reflect
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Instance = Any | BindingFlags.Instance;
        private const BindingFlags Static = Any | BindingFlags.Static | BindingFlags.FlattenHierarchy;

        // ----- types ------------------------------------------------------------------------

        private static Dictionary<string, Type> _types;
        private static int _assemblyCount;

        private static Dictionary<string, Type> Types
        {
            get
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                if (_types != null && assemblies.Length == _assemblyCount) return _types;

                _assemblyCount = assemblies.Length;
                _types = new Dictionary<string, Type>(StringComparer.Ordinal);
                foreach (var assembly in assemblies)
                {
                    Type[] types;
                    try { types = assembly.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    catch (Exception) { continue; }

                    // Later assemblies overwrite earlier ones, which is what makes a reloaded mod
                    // resolve to its live copy.
                    foreach (var t in types)
                    {
                        if (t.FullName == null || t.FullName.Contains("<")) continue;
                        _types[t.FullName.Replace('+', '.')] = t;
                        if (!t.IsNested || !_types.ContainsKey(t.Name)) _types[t.Name] = t;
                    }
                }
                return _types;
            }
        }

        public static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            return Types.TryGetValue(name.Trim(), out var type) ? type : null;
        }

        public static IEnumerable<Type> SearchTypes(string query) =>
            Types.Values.Distinct().Where(t => t.FullName.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                 .OrderBy(t => t.Name.Length).ThenBy(t => t.FullName);

        // ----- paths ------------------------------------------------------------------------

        // A path cut into its parts. "a.b[3].c()" is a, b, [3], c().
        private static List<string> Split(string path)
        {
            var parts = new List<string>();
            int i = 0;
            while (i < path.Length)
            {
                char c = path[i];
                if (c == '.') { i++; continue; }
                if (c == '[')
                {
                    int end = path.IndexOf(']', i);
                    if (end < 0) throw new DevException("unclosed [ in " + path);
                    parts.Add(path.Substring(i, end - i + 1));
                    i = end + 1;
                    continue;
                }
                int start = i;
                int depth = 0;
                while (i < path.Length && (depth > 0 || (path[i] != '.' && path[i] != '[')))
                {
                    if (path[i] == '(') depth++;
                    if (path[i] == ')') depth--;
                    i++;
                }
                parts.Add(path.Substring(start, i - start).Trim());
            }
            return parts;
        }

        // What a path's root is: either an object, or a type whose statics come next. Consumes as
        // many leading parts as the type name takes.
        private static (object target, Type type, int used) Root(List<string> parts)
        {
            string first = parts[0];
            if (first == "$hero") return (GameAccess.RequireHero(), null, 1);
            if (first == "$player") return (GameAccess.Player ?? throw new DevException("no local player"), null, 1);
            if (first.Length > 1 && first[0] == '$' && int.TryParse(first.Substring(1), out int handle)) return (Handles.Get(handle), null, 1);
            if (first.Length > 1 && first[0] == '#' && uint.TryParse(first.Substring(1), out uint netId)) return (GameAccess.RequireActor(netId), null, 1);

            // The longest run of leading parts that names a type: UnityEngine.Time before Time.
            for (int n = parts.Count; n >= 1; n--)
            {
                if (parts.Take(n).Any(p => p.StartsWith("[", StringComparison.Ordinal) || p.EndsWith(")", StringComparison.Ordinal))) continue;
                var type = FindType(string.Join(".", parts.Take(n)));
                if (type != null) return (null, type, n);
            }
            throw new DevException("'" + first + "' is not a type, $hero, $player, $N or #id - /reflect/types searches types");
        }

        public static object Get(string path)
        {
            var parts = Split(path);
            if (parts.Count == 0) throw new DevException("empty path");
            var (target, type, used) = Root(parts);
            if (used == parts.Count && target == null) return type;

            for (int i = used; i < parts.Count; i++)
            {
                (target, type) = Step(target, type, parts[i], path);
            }
            return target;
        }

        // Everything but the last member resolved, and the last member's name: what set and call
        // need, since they act on the member rather than read it.
        private static (object target, Type type, string last) Parent(string path)
        {
            var parts = Split(path);
            if (parts.Count < 2) throw new DevException("a path to a member, like Type.member or $hero.member");
            var (target, type, used) = Root(parts);
            if (used >= parts.Count) throw new DevException(path + " is a type; name a member of it");

            for (int i = used; i < parts.Count - 1; i++)
                (target, type) = Step(target, type, parts[i], path);
            return (target, type ?? target?.GetType(), parts[parts.Count - 1]);
        }

        private static (object, Type) Step(object target, Type staticType, string part, string path)
        {
            if (target == null && staticType == null) throw new DevException("null before '" + part + "' in " + path);
            if (target is UnityEngine.Object u && u == null) throw new DevException("destroyed object before '" + part + "' in " + path);

            if (part.StartsWith("[", StringComparison.Ordinal))
            {
                if (target == null) throw new DevException("cannot index a type in " + path);
                return (Index(target, part.Substring(1, part.Length - 2).Trim()), null);
            }

            if (part.EndsWith(")", StringComparison.Ordinal))
            {
                int open = part.IndexOf('(');
                string name = part.Substring(0, open).Trim();
                string inner = part.Substring(open + 1, part.Length - open - 2).Trim();
                var args = new JArray();
                foreach (var a in inner.Length > 0 ? inner.Split(',') : Array.Empty<string>())
                    args.Add(PathLiteral(a.Trim()));
                return (Invoke(target, staticType ?? target.GetType(), name, args, null), null);
            }

            var type = staticType ?? target.GetType();
            var flags = target == null ? Static : Instance;
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(part, flags | BindingFlags.DeclaredOnly);
                if (field != null) return (field.GetValue(target), null);
                var property = t.GetProperty(part, flags | BindingFlags.DeclaredOnly);
                if (property != null && property.GetIndexParameters().Length == 0) return (property.GetValue(target), null);
            }
            if (target == null)
            {
                // Statics inherited from a generic base - ManagerBase<T>.instance and the like.
                var field = type.GetField(part, Static);
                if (field != null) return (field.GetValue(null), null);
                var property = type.GetProperty(part, Static);
                if (property != null) return (property.GetValue(null), null);
            }

            // A nested type: DewMessageSettings.ButtonType.
            var nested = type.GetNestedType(part, Any);
            if (nested != null && target == null) return (null, nested);

            throw new DevException(type.Name + " has no " + (target == null ? "static " : "") + "field or property '" + part +
                                   "' - /reflect/members lists them");
        }

        // An argument written inside a path's parentheses: a number, a quoted string, true/false,
        // a handle, or a bare word - which is taken as a type name when one exists, since the
        // common case is GetComponent(HeroSkill).
        private static JToken PathLiteral(string text)
        {
            if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[text.Length - 1] == text[0])
                return text.Substring(1, text.Length - 2);
            if (text == "true" || text == "false") return text == "true";
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d % 1 == 0 && Math.Abs(d) < int.MaxValue ? (JToken)(long)d : d;
            return text;
        }

        private static object Index(object target, string key)
        {
            object keyValue = key.Length >= 2 && (key[0] == '"' || key[0] == '\'') ? key.Substring(1, key.Length - 2) :
                              int.TryParse(key, out int n) ? (object)n : key;

            if (target is Array array && keyValue is int ai) return array.GetValue(ai);
            if (target is IList list && keyValue is int li) return list[li];
            if (target is IDictionary dictionary && dictionary.Contains(keyValue)) return dictionary[keyValue];

            // Generic dictionaries and custom indexers (Mirror's SyncDictionary is not IDictionary).
            foreach (var indexer in target.GetType().GetProperties(Instance).Where(p => p.GetIndexParameters().Length == 1))
            {
                var parameterType = indexer.GetIndexParameters()[0].ParameterType;
                object converted;
                try { converted = Json.To(JToken.FromObject(keyValue), parameterType); }
                catch (Exception) { continue; }
                try { return indexer.GetValue(target, new[] { converted }); }
                catch (TargetInvocationException e) when (e.InnerException is KeyNotFoundException) { throw new DevException("no key " + key); }
            }

            // Anything enumerable, by position: HashSets have no indexer.
            if (target is IEnumerable sequence && keyValue is int position)
            {
                int i = 0;
                foreach (var item in sequence)
                    if (i++ == position) return item;
                throw new DevException("only " + i + " items");
            }
            throw new DevException(target.GetType().Name + " cannot be indexed by " + key);
        }

        // ----- set and call -----------------------------------------------------------------

        public static object Set(string path, JToken value)
        {
            var (target, type, last) = Parent(path);
            if (target is UnityEngine.Object u && u == null) throw new DevException("destroyed object in " + path);

            if (last.StartsWith("[", StringComparison.Ordinal))
            {
                if (target == null) throw new DevException("cannot index a type in " + path);
                return SetIndex(target, last.Substring(1, last.Length - 2).Trim(), value);
            }

            var flags = target == null ? Static : Instance;
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(last, flags | BindingFlags.DeclaredOnly);
                if (field != null)
                {
                    if (field.IsInitOnly || field.IsLiteral) throw new DevException(last + " is read-only");
                    field.SetValue(target, Json.To(value, field.FieldType));
                    return field.GetValue(target);
                }
                var property = t.GetProperty(last, flags | BindingFlags.DeclaredOnly);
                if (property != null)
                {
                    var setter = property.GetSetMethod(true);
                    if (setter == null) throw new DevException(last + " has no setter");
                    setter.Invoke(target, new[] { Json.To(value, property.PropertyType) });
                    return property.GetValue(target);
                }
            }
            throw new DevException(type.Name + " has no field or property '" + last + "'");
        }

        // An element of a list or an array, or an entry of a dictionary: through the type's own
        // indexer, which is what Mirror's SyncList and SyncDictionary need to send the change on.
        private static object SetIndex(object target, string key, JToken value)
        {
            object keyValue = key.Length >= 2 && (key[0] == '"' || key[0] == '\'') ? key.Substring(1, key.Length - 2) :
                              int.TryParse(key, out int n) ? (object)n : key;

            if (target is Array array && keyValue is int ai)
            {
                array.SetValue(Json.To(value, array.GetType().GetElementType()), ai);
                return array.GetValue(ai);
            }

            foreach (var indexer in target.GetType().GetProperties(Instance).Where(p => p.GetIndexParameters().Length == 1 && p.CanWrite))
            {
                object converted;
                try { converted = Json.To(JToken.FromObject(keyValue), indexer.GetIndexParameters()[0].ParameterType); }
                catch (Exception) { continue; }
                indexer.SetValue(target, Json.To(value, indexer.PropertyType), new[] { converted });
                return indexer.GetValue(target, new[] { converted });
            }
            throw new DevException(target.GetType().Name + " has no settable indexer for " + key);
        }

        public static object Call(string path, JArray args, JArray generic)
        {
            var (target, type, last) = Parent(path);
            if (target is UnityEngine.Object u && u == null) throw new DevException("destroyed object in " + path);
            return Invoke(target, type, last, args ?? new JArray(), generic);
        }

        // The first overload whose parameters the arguments convert to. Optional parameters may be
        // left out; a struct value type written back to its container is not attempted, so a
        // method on a struct field acts on a copy (as it would in C# through reflection).
        private static object Invoke(object target, Type type, string name, JArray args, JArray generic)
        {
            var flags = target == null ? Static : Instance;
            var candidates = new List<MethodInfo>();
            for (var t = type; t != null; t = t.BaseType)
                candidates.AddRange(t.GetMethods(flags | BindingFlags.DeclaredOnly).Where(m => m.Name == name));
            if (target == null) candidates.AddRange(type.GetMethods(Static).Where(m => m.Name == name && !candidates.Contains(m)));

            // GetComponent(HeroSkill) and friends: a type name in place of a Type argument, or
            // as the generic argument of the one-parameter generic form.
            if (candidates.Count == 0) throw new DevException(type.Name + " has no method '" + name + "' - /reflect/members lists them");

            var errors = new List<string>();
            foreach (var candidate in candidates.OrderBy(m => m.GetParameters().Length))
            {
                var method = candidate;
                if (method.IsGenericMethodDefinition)
                {
                    if (generic == null || generic.Count != method.GetGenericArguments().Length) continue;
                    var typeArgs = generic.Select(g => FindType((string)g) ?? throw new DevException("no type " + g)).ToArray();
                    try { method = method.MakeGenericMethod(typeArgs); }
                    catch (ArgumentException) { continue; }
                }

                var parameters = method.GetParameters();
                if (args.Count > parameters.Length) continue;
                if (parameters.Skip(args.Count).Any(p => !p.IsOptional)) continue;

                var values = new object[parameters.Length];
                bool fits = true;
                for (int i = 0; i < parameters.Length && fits; i++)
                {
                    if (i >= args.Count) { values[i] = parameters[i].DefaultValue; continue; }
                    var parameterType = parameters[i].ParameterType;
                    if (parameterType.IsByRef) parameterType = parameterType.GetElementType();
                    try { values[i] = Json.To(args[i], parameterType); }
                    catch (Exception e)
                    {
                        errors.Add(Signature(method) + ": argument " + i + ": " + (e is DevException ? e.Message : e.GetType().Name));
                        fits = false;
                    }
                }
                if (!fits) continue;

                var result = method.Invoke(target, values);
                return method.ReturnType == typeof(void) ? (object)new { returned = "void" } : result;
            }
            throw new DevException("no overload of " + type.Name + "." + name + " takes these arguments" +
                                   (errors.Count > 0 ? ": " + string.Join("; ", errors) : "") +
                                   " - overloads: " + string.Join(" | ", candidates.Select(Signature)));
        }

        public static string Signature(MethodInfo m) =>
            (m.IsStatic ? "static " : "") + Pretty(m.ReturnType) + " " + m.Name +
            (m.IsGenericMethodDefinition ? "<" + string.Join(",", m.GetGenericArguments().Select(a => a.Name)) + ">" : "") +
            "(" + string.Join(", ", m.GetParameters().Select(p => Pretty(p.ParameterType) + " " + p.Name + (p.IsOptional ? "?" : ""))) + ")";

        public static string Pretty(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.Name;
            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick > 0) name = name.Substring(0, tick);
            return name + "<" + string.Join(",", t.GetGenericArguments().Select(Pretty)) + ">";
        }

        public static object Members(object target, Type type, string filter, bool includePrivate)
        {
            // A type on its own shows both kinds of member: what it has, not only what can be reached
            // without an instance.
            var flags = (target == null ? Static | Instance : Instance) & ~(includePrivate ? 0 : BindingFlags.NonPublic);
            bool Keep(MemberInfo m) => string.IsNullOrEmpty(filter) || m.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            return new
            {
                type = type.FullName,
                baseTypes = BaseChain(type),
                fields = type.GetFields(flags).Where(Keep).Select(f => (f.IsStatic ? "static " : "") + Pretty(f.FieldType) + " " + f.Name).OrderBy(s => s),
                properties = type.GetProperties(flags).Where(Keep).Select(p => Pretty(p.PropertyType) + " " + p.Name +
                    (p.GetIndexParameters().Length > 0 ? "[" + string.Join(",", p.GetIndexParameters().Select(i => Pretty(i.ParameterType))) + "]" : "") +
                    " {" + (p.CanRead ? " get;" : "") + (p.CanWrite ? " set;" : "") + " }").OrderBy(s => s),
                methods = type.GetMethods(flags).Where(m => !m.IsSpecialName && Keep(m)).Select(Signature).OrderBy(s => s),
                nestedTypes = type.GetNestedTypes(Any).Where(Keep).Select(n => n.Name),
                enumValues = type.IsEnum ? Enum.GetNames(type) : null,
            };
        }

        private static IEnumerable<string> BaseChain(Type type)
        {
            for (var t = type.BaseType; t != null && t != typeof(object); t = t.BaseType) yield return Pretty(t);
        }
    }
}
#endif
