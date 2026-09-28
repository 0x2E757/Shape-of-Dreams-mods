#if DEBUG
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DevTools
{
    // Debug builds only: an HTTP/1.1 server on 127.0.0.1 through which an agent drives the game.
    // Hand-rolled on a TcpListener rather than HttpListener, which in Unity's Mono is a heavier
    // thing than the few dozen lines a JSON-in, JSON-out server needs.
    //
    // **Who can reach it.** The socket is bound to loopback, so nothing off this machine can. On
    // this machine, any process can - that is the price of a test tool and the reason it is not in
    // a Release build. What is shut out is the one visitor that is easy to forget: a web page.
    // A browser will send a "simple" POST to 127.0.0.1 from any site without asking, and with
    // /reflect/call on the other end that would be any site running code on this machine. So:
    //
    //   - a POST must say Content-Type: application/json, which a page can only send after a CORS
    //     preflight that this server never answers;
    //   - any request carrying Origin or Sec-Fetch-Site - headers only browsers send - is refused;
    //   - the Host header must name loopback, which defeats DNS rebinding.
    //
    // None of it costs curl, PowerShell or an agent anything.
    //
    // **Threads.** Connections are served on pool threads, and nothing the game owns may be
    // touched from those, so each request is queued and run in Update on the main thread while
    // its connection waits. A handler that needs frames to pass - a walk, a screenshot, a whole
    // run starting - returns an IEnumerator and is driven as a coroutine until it yields a Reply.
    //
    // Unity stops calling Update when the window loses focus, which it always has when commands
    // come from a terminal, so runInBackground is on for as long as this is loaded; and the Input
    // System, which by default drops keyboard state in the background, is told to ignore focus so
    // that the virtual keyboard keeps working.
    internal sealed class HttpServer : MonoBehaviour
    {
        public const int Port = 47653;

        private const int MaxHeaderBytes = 32 * 1024;
        private const int MaxBodyBytes = 4 * 1024 * 1024;
        private const float DefaultTimeout = 30f;
        private const float MaxTimeout = 600f;
        private const int DefaultDepth = 3;

        private sealed class Job
        {
            public string Method;
            public string Path;
            public Args Args;
            public int Status = 200;
            public JToken Body;
            public volatile bool Abandoned;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private bool _previousRunInBackground;
        private InputSettings.BackgroundBehavior? _previousBackground;

        private void OnEnable()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DevTools] agent API could not listen on 127.0.0.1:" + Port + ": " + e.Message);
                _listener = null;
                return;
            }

            _previousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;
            try
            {
                _previousBackground = InputSystem.settings.backgroundBehavior;
                InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DevTools] could not set the Input System's background behaviour: " + e.Message);
            }

            LogBuffer.Start();

            _running = true;
            _acceptThread = new Thread(Accept) { IsBackground = true, Name = "DevTools agent API" };
            _acceptThread.Start();
            Debug.Log("[DevTools] agent API on http://127.0.0.1:" + Port + "/ - GET / lists the routes");
        }

        private void OnDisable()
        {
            _running = false;
            DamageLog.Unsubscribe();
            MemoryUse.Unsubscribe();
            if (_listener != null)
            {
                try { _listener.Stop(); } catch (Exception) { }
                _listener = null;

                Application.runInBackground = _previousRunInBackground;
                if (_previousBackground.HasValue)
                {
                    try { InputSystem.settings.backgroundBehavior = _previousBackground.Value; } catch (Exception) { }
                }
            }
            if (_acceptThread != null && !_acceptThread.Join(1000)) Debug.LogWarning("[DevTools] agent API thread did not stop");
            _acceptThread = null;

            LogBuffer.Stop();
            VirtualMouse.Release();
            try { VirtualKeyboard.ReleaseAll(); } catch (Exception) { }

            // Anything still waiting gets an answer rather than a hang.
            while (_queue.TryDequeue(out var pending)) Fail(pending, 503, "server stopping");
        }

        // ----- main thread ------------------------------------------------------------------

        private void Update()
        {
            DamageLog.Tick();
            MemoryUse.Tick();
            while (_queue.TryDequeue(out var job))
            {
                if (job.Abandoned) continue;
                object result;
                bool isRoutine;
                try
                {
                    result = Router.Dispatch(job.Method, job.Path, job.Args, out isRoutine);
                }
                catch (Exception e)
                {
                    Fail(job, e);
                    continue;
                }

                if (isRoutine && result is IEnumerator routine) StartCoroutine(Drive(job, routine));
                else Finish(job, result);
            }
        }

        // A coroutine that turns the handler's exceptions into replies rather than into a
        // coroutine that silently stops. The yield sits outside the try, where C# allows it.
        private IEnumerator Drive(Job job, IEnumerator routine)
        {
            while (true)
            {
                bool more;
                object current = null;
                try
                {
                    more = routine.MoveNext();
                    if (more) current = routine.Current;
                }
                catch (Exception e)
                {
                    Fail(job, e);
                    yield break;
                }

                if (!more) { Finish(job, null); yield break; }
                if (current is Reply reply) { Finish(job, reply.Value); yield break; }
                if (job.Abandoned) yield break;
                yield return current;
            }
        }

        private static void Finish(Job job, object result)
        {
            int depth = Mathf.Clamp(job.Args.Int("depth", DefaultDepth), 0, 12);
            JToken body;
            try { body = Json.From(result, depth); }
            catch (Exception e) { Fail(job, e); return; }

            job.Body = new JObject { ["ok"] = true, ["result"] = body };
            job.Done.Set();
        }

        private static void Fail(Job job, Exception e)
        {
            while (e is TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
            if (e is DevException refused) Fail(job, refused.Status, refused.Message);
            else Fail(job, 500, e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace);
        }

        private static void Fail(Job job, int status, string message)
        {
            job.Status = status;
            job.Body = new JObject { ["ok"] = false, ["error"] = message };
            job.Done.Set();
        }

        // ----- sockets ----------------------------------------------------------------------

        private void Accept()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (Exception)
                {
                    // Stop() unblocks the accept by throwing; anything else ends the loop too.
                    return;
                }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        private void Serve(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    stream.ReadTimeout = 10000;

                    if (!TryReadRequest(stream, out var method, out var target, out var headers, out var body, out var bad))
                    {
                        Write(stream, 400, Error(bad));
                        return;
                    }

                    string refusal = Refuse(method, headers);
                    if (refusal != null)
                    {
                        Write(stream, 403, Error(refusal));
                        return;
                    }

                    SplitTarget(target, out string path, out JObject query);
                    JObject args = query;
                    if (body.Length > 0)
                    {
                        JToken parsed;
                        try { parsed = JToken.Parse(Encoding.UTF8.GetString(body)); }
                        catch (JsonException e) { Write(stream, 400, Error("the body is not JSON: " + e.Message)); return; }
                        if (!(parsed is JObject obj)) { Write(stream, 400, Error("the body must be a JSON object")); return; }
                        obj.Merge(query, new JsonMergeSettings { MergeArrayHandling = MergeArrayHandling.Replace });
                        args = obj;
                    }

                    var job = new Job { Method = method, Path = path, Args = new Args(args) };
                    float timeout = Mathf.Clamp(job.Args.Float("timeout", DefaultTimeout), 1f, MaxTimeout);

                    _queue.Enqueue(job);
                    if (!job.Done.Wait(TimeSpan.FromSeconds(timeout)))
                    {
                        job.Abandoned = true;
                        Write(stream, 504, Error("timed out after " + timeout + "s waiting for the game - pass a larger 'timeout', or check that the game is not paused on a loading screen"));
                        return;
                    }
                    Write(stream, job.Status, job.Body);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[DevTools] agent API connection failed: " + e.Message);
                }
            }
        }

        private static string Refuse(string method, Dictionary<string, string> headers)
        {
            if (headers.ContainsKey("origin") || headers.ContainsKey("sec-fetch-site"))
                return "requests from a web browser are refused";

            if (headers.TryGetValue("host", out var host))
            {
                string name = host;
                int colon = name.LastIndexOf(':');
                if (colon > 0 && !name.EndsWith("]", StringComparison.Ordinal)) name = name.Substring(0, colon);
                if (name != "127.0.0.1" && !name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                    return "Host must be 127.0.0.1 or localhost";
            }

            if (method == "POST")
            {
                headers.TryGetValue("content-type", out var type);
                if (type == null || !type.TrimStart().StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
                    return "POST needs Content-Type: application/json";
            }
            else if (method != "GET")
            {
                return "only GET and POST";
            }
            return null;
        }

        private static bool TryReadRequest(Stream stream, out string method, out string target,
                                           out Dictionary<string, string> headers, out byte[] body, out string bad)
        {
            method = target = bad = null;
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            body = Array.Empty<byte>();

            // Byte by byte up to the blank line, so that nothing of the body is read into a buffer
            // that the Content-Length count then misses.
            var head = new MemoryStream();
            int last4 = 0;
            while (last4 != 0x0D0A0D0A)
            {
                int b = stream.ReadByte();
                if (b < 0) { bad = "connection closed mid-request"; return false; }
                head.WriteByte((byte)b);
                if (head.Length > MaxHeaderBytes) { bad = "headers too large"; return false; }
                last4 = (last4 << 8) | b;
            }

            var lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
            var request = lines[0].Split(' ');
            if (request.Length < 2) { bad = "bad request line"; return false; }
            method = request[0].ToUpperInvariant();
            target = request[1];

            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i].Substring(0, colon).Trim()] = lines[i].Substring(colon + 1).Trim();
            }

            if (headers.TryGetValue("transfer-encoding", out var te) && te.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                bad = "chunked bodies are not supported - send Content-Length";
                return false;
            }

            if (headers.TryGetValue("content-length", out var lengthText))
            {
                if (!int.TryParse(lengthText, out int length) || length < 0 || length > MaxBodyBytes) { bad = "bad Content-Length"; return false; }
                body = new byte[length];
                int read = 0;
                while (read < length)
                {
                    int n = stream.Read(body, read, length - read);
                    if (n <= 0) { bad = "connection closed mid-body"; return false; }
                    read += n;
                }
            }
            return true;
        }

        // The query string as JSON: numbers, booleans and JSON literals as themselves, anything
        // else as a string - so ?x=1.5&wait=true reads the same as {"x":1.5,"wait":true}.
        private static void SplitTarget(string target, out string path, out JObject query)
        {
            query = new JObject();
            int mark = target.IndexOf('?');
            path = Uri.UnescapeDataString(mark >= 0 ? target.Substring(0, mark) : target);
            if (mark < 0) return;

            foreach (var part in target.Substring(mark + 1).Split('&'))
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = Uri.UnescapeDataString((eq >= 0 ? part.Substring(0, eq) : part).Replace('+', ' '));
                string value = eq >= 0 ? Uri.UnescapeDataString(part.Substring(eq + 1).Replace('+', ' ')) : "true";
                query[key] = Literal(value);
            }
        }

        private static JToken Literal(string value)
        {
            string t = value.Trim();
            if (t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '-' || t[0] == '{' || t[0] == '[' || t == "true" || t == "false" || t == "null"))
            {
                try { return JToken.Parse(t); } catch (JsonException) { }
            }
            return value;
        }

        private static JToken Error(string message) => new JObject { ["ok"] = false, ["error"] = message };

        private static void Write(Stream stream, int status, JToken body)
        {
            var bytes = new UTF8Encoding(false).GetBytes(body.ToString(Formatting.Indented) + "\n");
            string head = "HTTP/1.1 " + status + " " + Reason(status) + "\r\n" +
                          "Content-Type: application/json; charset=utf-8\r\n" +
                          "Content-Length: " + bytes.Length + "\r\n" +
                          "Cache-Control: no-store\r\n" +
                          "Connection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                case 504: return "Gateway Timeout";
                default: return "Status";
            }
        }
    }
}
#endif
