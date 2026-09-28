#if DEBUG
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DevTools
{
    // Objects the agent has been shown, by number, so that it can hand one back: "$12" in a
    // reflection path or an argument is whatever was shown as {"$ref": 12}. Strong references,
    // capped - a dev session that shows more than the cap starts again from 1, and says so.
    internal static class Handles
    {
        private const int Cap = 50000;

        private sealed class ByReference : IEqualityComparer<object>
        {
            public new bool Equals(object a, object b) => ReferenceEquals(a, b);
            public int GetHashCode(object o) => RuntimeHelpers.GetHashCode(o);
        }

        private static readonly Dictionary<int, object> ById = new Dictionary<int, object>();
        private static readonly Dictionary<object, int> ByObject = new Dictionary<object, int>(new ByReference());
        private static int _next = 1;

        public static int Put(object o)
        {
            if (ByObject.TryGetValue(o, out int id)) return id;
            if (ById.Count >= Cap)
            {
                Debug.Log("[DevTools] handle table full - old $refs are no longer valid");
                ById.Clear();
                ByObject.Clear();
            }
            id = _next++;
            ById[id] = o;
            ByObject[o] = id;
            return id;
        }

        public static object Get(int id)
        {
            if (!ById.TryGetValue(id, out var o)) throw new DevException("no object $" + id + " - handles are only valid for this game session");
            if (o is UnityEngine.Object u && u == null) throw new DevException("$" + id + " has been destroyed");
            return o;
        }

        public static void Clear()
        {
            ById.Clear();
            ByObject.Clear();
        }
    }

    // Anything to JSON, for showing the agent. Newtonsoft's own serializer is no use on Unity
    // types - Vector3.normalized is a Vector3, so it recurses until it throws - so this walks
    // objects itself, to a depth, and past that depth gives a {"$ref"} handle instead.
    //
    // Unity's own components are shown by name only, with a few exceptions: their properties are
    // not safe to read blind (Renderer.material makes a copy of the material on every read), and
    // they are rarely what is wanted anyway. The game's own types are read in full.
    internal static class Json
    {
        private const int MaxItems = 200;

        public static JToken From(object o, int depth = 2)
        {
            try { return Write(o, depth); }
            catch (Exception e) { return "<" + e.GetType().Name + ": " + e.Message + ">"; }
        }

        public static JObject Ref(object o)
        {
            var r = new JObject { ["$ref"] = Handles.Put(o), ["type"] = o.GetType().Name };
            if (o is UnityEngine.Object u) r["name"] = u.name;
            if (o is Actor actor && actor.netId != 0) r["id"] = actor.netId;
            return r;
        }

        private static JToken Write(object o, int depth)
        {
            switch (o)
            {
                case null: return JValue.CreateNull();
                case JToken token: return token;
                case string s: return s;
                case bool b: return b;
                case float f: return Number(f);
                case double d: return float.IsNaN((float)d) || double.IsInfinity(d) ? (JToken)d.ToString() : d;
                case decimal m: return m;
                case char c: return c.ToString();
                case Enum e: return e.ToString();
                case Type t: return t.FullName;
                case Vector2 v: return new JObject { ["x"] = Number(v.x), ["y"] = Number(v.y) };
                case Vector3 v: return new JObject { ["x"] = Number(v.x), ["y"] = Number(v.y), ["z"] = Number(v.z) };
                case Vector4 v: return new JObject { ["x"] = Number(v.x), ["y"] = Number(v.y), ["z"] = Number(v.z), ["w"] = Number(v.w) };
                case Vector2Int v: return new JObject { ["x"] = v.x, ["y"] = v.y };
                case Vector3Int v: return new JObject { ["x"] = v.x, ["y"] = v.y, ["z"] = v.z };
                case Quaternion q: return Write(q.eulerAngles, depth);
                case Color c: return new JObject { ["r"] = Number(c.r), ["g"] = Number(c.g), ["b"] = Number(c.b), ["a"] = Number(c.a) };
                case Rect r: return new JObject { ["x"] = Number(r.x), ["y"] = Number(r.y), ["w"] = Number(r.width), ["h"] = Number(r.height) };
                case Delegate del: return "<delegate " + del.Method.Name + ">";
            }

            var type = o.GetType();
            if (type.IsPrimitive) return JToken.FromObject(o);

            // The API's own replies are anonymous objects, and are containers rather than things:
            // they take no handle and use up no depth.
            if (IsAnonymous(type)) return WriteMembers(o, type, depth, anonymous: true);

            if (o is UnityEngine.Object unity)
            {
                if (unity == null) return JValue.CreateNull();
                if (depth <= 0) return Ref(o);
                if (o is GameObject go) return WriteGameObject(go, depth);
                if (o is Transform tr) return WriteTransform(tr);
                if (IsEngineType(type)) return Ref(o);
            }
            else if (depth <= 0 && !IsPlainValue(type))
            {
                return Ref(o);
            }
            else if (depth < -2)
            {
                // A struct whose property is the same struct (as Vector3.normalized is) would
                // otherwise never stop.
                return o.ToString();
            }

            if (o is IDictionary dictionary)
            {
                var result = new JObject();
                int n = 0;
                foreach (DictionaryEntry pair in dictionary)
                {
                    if (n++ >= MaxItems) { result["..."] = dictionary.Count - MaxItems + " more"; break; }
                    result[pair.Key?.ToString() ?? "null"] = Write(pair.Value, depth - 1);
                }
                return result;
            }

            if (o is IEnumerable sequence)
            {
                var result = new JArray();
                int n = 0;
                foreach (var item in sequence)
                {
                    if (n++ >= MaxItems) { result.Add("... more"); break; }
                    // Key/value pairs from a generic dictionary that is not IDictionary.
                    result.Add(Write(item, depth - 1));
                }
                return result;
            }

            return WriteMembers(o, type, depth - 1, anonymous: false);
        }

        private static bool IsAnonymous(Type type) =>
            type.Name.Contains("AnonymousType") && type.IsDefined(typeof(CompilerGeneratedAttribute), false);

        private static JToken Number(float f) => float.IsNaN(f) || float.IsInfinity(f) ? (JToken)f.ToString() : Math.Round(f, 4);

        // Structs of plain data are worth writing out even at the depth limit: a KeyValuePair or
        // a WorldNodeData as a $ref is useless.
        private static bool IsPlainValue(Type type) => type.IsValueType;

        private static bool IsEngineType(Type type)
        {
            string ns = type.Namespace ?? "";
            return ns.StartsWith("UnityEngine", StringComparison.Ordinal) || ns.StartsWith("TMPro", StringComparison.Ordinal) ||
                   ns.StartsWith("Cinemachine", StringComparison.Ordinal);
        }

        private static JObject WriteGameObject(GameObject go, int depth)
        {
            return new JObject
            {
                ["$ref"] = Handles.Put(go),
                ["type"] = "GameObject",
                ["name"] = go.name,
                ["active"] = go.activeInHierarchy,
                ["position"] = Write(go.transform.position, 1),
                ["components"] = new JArray(go.GetComponents<Component>().Where(c => c != null).Select(c => (JToken)Ref(c))),
            };
        }

        private static JObject WriteTransform(Transform t)
        {
            return new JObject
            {
                ["$ref"] = Handles.Put(t),
                ["type"] = "Transform",
                ["name"] = t.name,
                ["position"] = Write(t.position, 1),
                ["rotation"] = Write(t.eulerAngles, 1),
                ["scale"] = Write(t.localScale, 1),
                ["parent"] = t.parent != null ? Ref(t.parent) : null,
                ["childCount"] = t.childCount,
            };
        }

        private static readonly Dictionary<Type, MemberInfo[]> MembersCache = new Dictionary<Type, MemberInfo[]>();

        private static JObject WriteMembers(object o, Type type, int memberDepth, bool anonymous)
        {
            var result = new JObject();
            if (!anonymous)
            {
                if (!type.IsValueType) result["$ref"] = Handles.Put(o);
                result["$type"] = type.Name;
                if (o is Actor actor && actor.netId != 0) result["id"] = actor.netId;
            }

            foreach (var member in Members(type))
            {
                object value;
                try
                {
                    value = member is FieldInfo field ? field.GetValue(o) : ((PropertyInfo)member).GetValue(o);
                }
                catch (Exception e)
                {
                    var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                    result[member.Name] = "<" + inner.GetType().Name + ">";
                    continue;
                }
                result[member.Name] = Write(value, memberDepth);
            }
            return result;
        }

        // Public instance fields and readable properties, the game's side of the hierarchy only:
        // walking stops at MonoBehaviour, so an Entity shows what makes it an Entity and not a
        // hundred inherited engine properties.
        public static MemberInfo[] Members(Type type)
        {
            if (MembersCache.TryGetValue(type, out var cached)) return cached;

            const BindingFlags Flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var list = new List<MemberInfo>();
            var seen = new HashSet<string>();
            for (var t = type; t != null && t != typeof(object) && t != typeof(MonoBehaviour) && t != typeof(Component) &&
                               t != typeof(UnityEngine.Object) && !IsEngineType(t) && t.Namespace != "Mirror"; t = t.BaseType)
            {
                foreach (var f in t.GetFields(Flags))
                    if (seen.Add(f.Name)) list.Add(f);
                foreach (var p in t.GetProperties(Flags))
                    if (p.CanRead && p.GetIndexParameters().Length == 0 && p.GetGetMethod() != null && seen.Add(p.Name)) list.Add(p);
            }
            var members = list.ToArray();
            MembersCache[type] = members;
            return members;
        }

        // ----- reading arguments ------------------------------------------------------------

        // A JSON value as the given type: "$12" or {"$ref": 12} is a handle, "#34" an actor by id,
        // {"x","y","z"} a vector, a string an enum name - and anything else what Newtonsoft makes
        // of it.
        public static object To(JToken token, Type type)
        {
            if (token == null || token.Type == JTokenType.Null)
                return type.IsValueType && Nullable.GetUnderlyingType(type) == null ? Activator.CreateInstance(type) : null;

            var target = Nullable.GetUnderlyingType(type) ?? type;

            object handle = AsHandle(token);
            if (handle != null)
            {
                if (target.IsInstanceOfType(handle)) return handle;
                if (handle is Component component && typeof(Component).IsAssignableFrom(target))
                {
                    var sibling = component.GetComponent(target);
                    if (sibling != null) return sibling;
                }
                if (handle is GameObject go && typeof(Component).IsAssignableFrom(target))
                {
                    var c = go.GetComponent(target);
                    if (c != null) return c;
                }
                throw new DevException(handle.GetType().Name + " is not a " + target.Name);
            }

            if (target == typeof(object)) return token.ToObject<object>();
            if (target.IsEnum && token.Type == JTokenType.String) return Enum.Parse(target, (string)token, true);
            if (target == typeof(Vector3) && token is JObject v3)
                return new Vector3((float?)v3["x"] ?? 0f, (float?)v3["y"] ?? 0f, (float?)v3["z"] ?? 0f);
            if (target == typeof(Vector2) && token is JObject v2)
                return new Vector2((float?)v2["x"] ?? 0f, (float?)v2["y"] ?? 0f);
            if (target == typeof(Quaternion) && token is JObject q)
                return Quaternion.Euler((float?)q["x"] ?? 0f, (float?)q["y"] ?? 0f, (float?)q["z"] ?? 0f);
            if (target == typeof(Type) && token.Type == JTokenType.String)
                return Reflect.FindType((string)token) ?? throw new DevException("no type " + token);

            return token.ToObject(target);
        }

        private static object AsHandle(JToken token)
        {
            if (token.Type == JTokenType.String)
            {
                string s = (string)token;
                if (s.Length > 1 && s[0] == '$' && int.TryParse(s.Substring(1), out int id)) return Handles.Get(id);
                if (s.Length > 1 && s[0] == '#' && uint.TryParse(s.Substring(1), out uint netId)) return GameAccess.RequireActor(netId);
                if (s == "$hero") return GameAccess.RequireHero();
                if (s == "$player") return GameAccess.Player ?? throw new DevException("no local player");
            }
            if (token is JObject obj && obj["$ref"] != null && obj["$ref"].Type == JTokenType.Integer)
                return Handles.Get((int)obj["$ref"]);
            return null;
        }
    }
}
#endif
