#if DEBUG
using System;
using System.Collections;
using System.Linq;
using IngameDebugConsole;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace DevTools
{
    // The escape hatch: anything the semantic routes do not cover, by reflection. See Reflect for
    // the path syntax. Also the game's own debug console, and the log.
    internal static class ReflectApi
    {
        [Route("GET", "/reflect/get", "Read a value by path, e.g. ZoneManager.instance.currentNodeIndex or $hero.Status.statusEffects.",
               "path, depth=2")]
        private static object Get(Args a) => Json.From(Reflect.Get(a.Str("path")), a.Int("depth", 2));

        [Route("POST", "/reflect/set", "Write a field or property by path. Returns the value read back.", "path, value")]
        private static object Set(Args a) =>
            Json.From(Reflect.Set(a.Str("path"), a.Token("value") ?? JValue.CreateNull()), a.Int("depth", 1));

        [Route("POST", "/reflect/call", "Call a method by path, e.g. $hero.Control.CmdStop. Arguments are JSON; \"$N\", \"#id\", \"$hero\" pass objects; {x,y,z} is a Vector3; a string is an enum name or a type name.",
               "path, args=[], generic=[type names], depth=2")]
        private static object Call(Args a) =>
            Json.From(Reflect.Call(a.Str("path"), a.Token("args") as JArray, a.Token("generic") as JArray), a.Int("depth", 2));

        [Route("GET", "/reflect/members", "Fields, properties and methods of what a path points at (or of a type by name).",
               "path, filter?, private=false")]
        private static object Members(Args a)
        {
            var value = Reflect.Get(a.Str("path"));
            if (value == null) throw new DevException(a.Str("path") + " is null");
            var type = value as Type;
            return Reflect.Members(type != null ? null : value, type ?? value.GetType(), a.Str("filter", null), a.Bool("private"));
        }

        [Route("GET", "/reflect/types", "Search loaded types by name.", "query, limit=50")]
        private static object Types(Args a) =>
            Reflect.SearchTypes(a.Str("query")).Take(a.Int("limit", 50))
                   .Select(t => new { name = t.FullName, assembly = t.Assembly.GetName().Name, kind = Kind(t) });

        private static string Kind(Type t) =>
            t.IsEnum ? "enum" : t.IsInterface ? "interface" : t.IsValueType ? "struct" :
            typeof(Component).IsAssignableFrom(t) ? "component" : typeof(UnityEngine.Object).IsAssignableFrom(t) ? "asset" : "class";

        [Route("GET", "/reflect/find", "Live objects of a Unity type in the scene, as handles.", "type, inactive=false, limit=100, name?")]
        private static object Find(Args a)
        {
            var type = Reflect.FindType(a.Str("type")) ?? throw new DevException("no type " + a.Str("type"));
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new DevException(type.Name + " is not a Unity object");

            string name = a.Str("name", null);
            var found = UnityEngine.Object.FindObjectsByType(type, a.Bool("inactive") ? FindObjectsInactive.Include : FindObjectsInactive.Exclude,
                                                            FindObjectsSortMode.None)
                                          .Where(o => o != null && (name == null || o.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                                          .ToList();
            return new { count = found.Count, objects = found.Take(a.Int("limit", 100)).Select(o => Json.Ref(o)) };
        }

        [Route("GET", "/handle", "What a handle points at now.", "id (the N of $N), depth=2")]
        private static object Handle(Args a) => Json.From(Handles.Get(a.Int("id")), a.Int("depth", 2));

        // ----- the game's console -------------------------------------------------------------

        [Route("POST", "/console", "Run a command in the game's own debug console (IngameDebugConsole) and return what it logged. 'help' lists them.",
               "command, wait=0.3 (seconds to collect output)")]
        private static IEnumerator Console(Args a)
        {
            string command = a.Str("command");
            long from = LogBuffer.Next;
            DebugLogConsole.ExecuteCommand(command);

            // Server commands go out over the network even in solo and answer a frame or two later.
            yield return new WaitForSecondsRealtime(Mathf.Clamp(a.Float("wait", 0.3f), 0f, 10f));
            yield return new Reply(new
            {
                command,
                output = LogBuffer.Since(from, 500, false, null).Select(l => l.stack != null ? l.message + "\n" + l.stack : l.message),
            });
        }

        // ----- mods ------------------------------------------------------------------------

        [Route("GET", "/mods", "The mods loaded right now.")]
        private static object Mods(Args a) =>
            DewMod.loadedInstances.Select(i => new { id = i.mod.metadata.id, name = i.mod.metadata.name, version = i.mod.metadata.modVer });

        // The game reloads mods itself when their files change - but only while its window has
        // focus, which it never has when an agent is at a terminal. This is the same reload, on
        // request. It unloads this mod too, so it is done a moment after answering, from a
        // coroutine the game owns rather than one that is about to be destroyed.
        [Route("POST", "/mods/reload", "Reload every mod from disk, as the game does after a rebuild when focused. The API is gone for a second or two while this mod reloads.")]
        private static object Reload(Args a)
        {
            Dew.GetCoroutiner().StartCoroutine(ReloadSoon());
            return new { reloading = true, note = "poll GET /state until it answers again" };
        }

        private static IEnumerator ReloadSoon()
        {
            yield return new WaitForSecondsRealtime(0.3f);
            DewMod.Refresh();
            DewMod.ReloadFromActiveMods();
        }

        // ----- the log ----------------------------------------------------------------------

        [Route("GET", "/log", "Unity log lines, numbered. Pass since=<next> from the previous reply to get only new ones.",
               "since=0, limit=200, errors=false, contains?")]
        private static object Log(Args a)
        {
            var lines = LogBuffer.Since((long)a.Float("since", 0f), Mathf.Clamp(a.Int("limit", 200), 1, 4000), a.Bool("errors"), a.Str("contains", null));
            return new { next = LogBuffer.Next, lines = lines.Select(l => new { l.seq, l.time, l.level, l.message, l.stack }) };
        }
    }
}
#endif
