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
    // Marks a static method as an API endpoint. The method takes the request's arguments and
    // returns anything Json.From can write - or an IEnumerator, which is run as a coroutine and
    // answers when it yields a Reply (see HttpServer).
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class RouteAttribute : Attribute
    {
        public readonly string Method;
        public readonly string Path;
        public readonly string Summary;
        public readonly string Params;

        // GET routes read and change nothing; everything that changes anything is POST, which a
        // web page cannot send here (see HttpServer's guards).
        public RouteAttribute(string method, string path, string summary, string @params = "")
        {
            Method = method;
            Path = path;
            Summary = summary;
            Params = @params;
        }
    }

    // What a deferred handler yields to answer.
    internal sealed class Reply
    {
        public readonly object Value;
        public Reply(object value) { Value = value; }
    }

    // A request's arguments: the JSON body of a POST, or the query string of a GET, or both.
    internal sealed class Args
    {
        public readonly JObject Raw;

        public Args(JObject raw) { Raw = raw ?? new JObject(); }

        public bool Has(string name) => Raw.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) && t.Type != JTokenType.Null;

        public JToken Token(string name) =>
            Raw.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out var t) && t.Type != JTokenType.Null ? t : null;

        public string Str(string name)
        {
            var t = Token(name) ?? throw new DevException("missing '" + name + "'");
            return t.Type == JTokenType.String ? (string)t : t.ToString(Newtonsoft.Json.Formatting.None);
        }

        public string Str(string name, string fallback) => Has(name) ? Str(name) : fallback;

        public int Int(string name) => (int)Math.Round(Float(name));
        public int Int(string name, int fallback) => Has(name) ? Int(name) : fallback;
        public uint Id(string name) => (uint)Int(name);

        public float Float(string name)
        {
            var t = Token(name) ?? throw new DevException("missing '" + name + "'");
            if (t.Type == JTokenType.Integer || t.Type == JTokenType.Float) return (float)t;
            if (float.TryParse((string)t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)) return f;
            throw new DevException("'" + name + "' is not a number");
        }

        public float Float(string name, float fallback) => Has(name) ? Float(name) : fallback;

        public bool Bool(string name, bool fallback = false)
        {
            var t = Token(name);
            if (t == null) return fallback;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            string s = t.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes" || s == "on";
        }

        // A point on the ground from x and z (y optional), or null when neither is given.
        public Vector3? Point()
        {
            if (!Has("x") && !Has("z")) return null;
            return GameAccess.Ground(Float("x"), Float("z"), Has("y") ? Float("y") : (float?)null);
        }
    }

    internal sealed class Route
    {
        public RouteAttribute Info;
        public Func<Args, object> Handler;
        public bool IsRoutine;
    }

    internal static class Router
    {
        private static Dictionary<string, Route> _routes;

        public static IEnumerable<Route> All
        {
            get { Build(); return _routes.Values.OrderBy(r => r.Info.Path); }
        }

        private static void Build()
        {
            if (_routes != null) return;
            _routes = new Dictionary<string, Route>(StringComparer.OrdinalIgnoreCase);

            const BindingFlags Statics = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var type in typeof(Router).Assembly.GetTypes())
            foreach (var method in type.GetMethods(Statics))
            {
                var info = method.GetCustomAttribute<RouteAttribute>();
                if (info == null) continue;

                var handler = (Func<Args, object>)Delegate.CreateDelegate(typeof(Func<Args, object>), method);
                if (_routes.ContainsKey(info.Path)) Debug.LogWarning("[DevTools] two routes for " + info.Path);
                // Decided by the declared type, not the returned object: a LINQ query is an IEnumerator
                // too, and a route answering with one must not be run as a coroutine.
                _routes[info.Path] = new Route { Info = info, Handler = handler, IsRoutine = typeof(IEnumerator).IsAssignableFrom(method.ReturnType) };
            }
        }

        public static Route Find(string path)
        {
            Build();
            string key = path.Length > 1 ? path.TrimEnd('/') : path;
            return _routes.TryGetValue(key, out var route) ? route : null;
        }

        public static object Dispatch(string method, string path, Args args, out bool isRoutine)
        {
            var route = Find(path);
            if (route == null)
            {
                var near = All.Where(r => r.Info.Path.StartsWith("/" + path.Trim('/').Split('/')[0], StringComparison.OrdinalIgnoreCase))
                              .Select(r => r.Info.Method + " " + r.Info.Path).Take(12).ToArray();
                throw new DevException("no route " + path + (near.Length > 0 ? " - did you mean: " + string.Join(", ", near) : " - GET / lists them"), 404);
            }

            // A GET route may also be POSTed, for clients that would rather send JSON; a POST
            // route is never a GET.
            if (route.Info.Method == "POST" && method != "POST")
                throw new DevException(path + " changes the game and takes POST with a JSON body", 405);

            isRoutine = route.IsRoutine;
            return route.Handler(args);
        }

        [Route("GET", "/", "Every route, with its parameters. Start here.")]
        private static object Index(Args a)
        {
            return new
            {
                server = "DevTools agent API",
                conventions = new[]
                {
                    "POST bodies are JSON objects with Content-Type: application/json; GET takes the same arguments as a query string.",
                    "Replies are {ok:true, result} or {ok:false, error}. Actors (heroes, monsters, items, shrines) are identified by 'id' (Mirror netId).",
                    "Objects shown as {$ref:N} can be passed back as \"$N\" in /reflect paths and arguments; \"#id\" is an actor by id; \"$hero\" and \"$player\" are the local ones.",
                    "Positions are world space; y is height, the ground plane is x/z. Screen positions are pixels from the bottom-left.",
                    "Any request takes 'timeout' in seconds (default 30, max 600) for how long to wait for the game.",
                },
                routes = All.Select(r => new { method = r.Info.Method, path = r.Info.Path, summary = r.Info.Summary, @params = r.Info.Params }),
            };
        }
    }
}
#endif
