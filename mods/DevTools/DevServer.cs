#if DEBUG
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace DevTools
{
    // Debug builds only: a command line into the running game, for driving a test from outside
    // it. One connection is one command - a line of text in, the answer back, then the connection
    // closes - and tools/devcmd.ps1 is the client.
    //
    // **Loopback only.** The listener is bound to 127.0.0.1, so nothing off this machine can reach
    // it, and it exists only in a Debug build, which is never what gets published. Anything
    // running on this machine can reach it, though, and it can change the hero's loadout; that is
    // the price of a test tool and the reason it is not in a Release build.
    //
    // The socket is served on a background thread, and nothing the game owns may be touched from
    // there, so a command is queued and run in Update on the main thread, and the socket thread
    // waits for the answer.
    //
    // Unity stops calling Update when the window loses focus, and the window is never focused
    // when a command arrives from a terminal. So Application.runInBackground is switched on for as
    // long as this is loaded and put back afterwards.
    internal sealed class DevServer : MonoBehaviour
    {
        public const int Port = 47653;

        // Long enough for a command that spawns things; short enough that a stuck frame does not
        // hang the client forever.
        private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

        private sealed class Request
        {
            public string Line;
            public string Reply;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        private readonly ConcurrentQueue<Request> _queue = new ConcurrentQueue<Request>();
        private TcpListener _listener;
        private Thread _thread;
        private volatile bool _running;
        private bool _previousRunInBackground;

        private void OnEnable()
        {
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[DevTools] command server could not listen on 127.0.0.1:" + Port + ": " + e.Message);
                _listener = null;
                return;
            }

            _previousRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            _running = true;
            _thread = new Thread(Serve) { IsBackground = true, Name = "DevTools command server" };
            _thread.Start();
            Debug.Log("[DevTools] command server on 127.0.0.1:" + Port);
        }

        private void OnDisable()
        {
            _running = false;
            if (_listener != null)
            {
                try { _listener.Stop(); } catch (Exception) { }
                _listener = null;
                Application.runInBackground = _previousRunInBackground;
            }
            if (_thread != null && !_thread.Join(1000)) Debug.LogWarning("[DevTools] command server thread did not stop");
            _thread = null;

            // Anything still waiting gets an answer rather than a ten-second hang.
            while (_queue.TryDequeue(out var pending))
            {
                pending.Reply = "server stopping";
                pending.Done.Set();
            }
        }

        private void Update()
        {
            while (_queue.TryDequeue(out var request))
            {
                try { request.Reply = DevCommands.Run(request.Line); }
                catch (Exception e) { request.Reply = "error: " + e; }
                request.Done.Set();
            }
        }

        private void Serve()
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

                using (client)
                {
                    try
                    {
                        var stream = client.GetStream();
                        stream.ReadTimeout = 5000;
                        var reader = new StreamReader(stream, new UTF8Encoding(false));
                        string line = reader.ReadLine();

                        var request = new Request { Line = line };
                        _queue.Enqueue(request);
                        string reply = request.Done.Wait(Wait) ? request.Reply : "timed out waiting for the game";

                        var bytes = new UTF8Encoding(false).GetBytes((reply ?? string.Empty) + "\n");
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    catch (Exception e)
                    {
                        Debug.LogWarning("[DevTools] command connection failed: " + e.Message);
                    }
                }
            }
        }
    }
}
#endif
