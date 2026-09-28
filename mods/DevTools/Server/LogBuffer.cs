#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace DevTools
{
    // The last few thousand lines of the Unity log, numbered, so an agent can ask "what has been
    // logged since line N" instead of reading Player.log from disk. Filled from any thread, which
    // is what logMessageReceivedThreaded is for, hence the lock.
    internal static class LogBuffer
    {
        private const int Capacity = 4000;
        private const int MaxStackChars = 2000;

        public struct Line
        {
            public long seq;
            public double time;
            public string level;
            public string message;
            public string stack;
        }

        private static readonly object Gate = new object();
        private static readonly Queue<Line> Lines = new Queue<Line>();
        private static long _next = 1;
        private static bool _started;

        // Seconds since the server started. Not Time.realtimeSinceStartup, which throws off the main
        // thread - and this is called from whichever thread logged.
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

        public static long Next
        {
            get { lock (Gate) return _next; }
        }

        public static void Start()
        {
            if (_started) return;
            _started = true;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;
            Application.logMessageReceivedThreaded -= OnLog;
        }

        private static void OnLog(string message, string stack, LogType type)
        {
            bool error = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
            lock (Gate)
            {
                Lines.Enqueue(new Line
                {
                    seq = _next++,
                    time = Math.Round(Clock.Elapsed.TotalSeconds, 3),
                    level = type.ToString(),
                    message = message,
                    stack = error && !string.IsNullOrEmpty(stack)
                        ? (stack.Length > MaxStackChars ? stack.Substring(0, MaxStackChars) : stack)
                        : null,
                });
                while (Lines.Count > Capacity) Lines.Dequeue();
            }
        }

        public static List<Line> Since(long seq, int limit, bool errorsOnly, string contains)
        {
            lock (Gate)
            {
                IEnumerable<Line> lines = Lines.Where(l => l.seq >= seq);
                if (errorsOnly) lines = lines.Where(l => l.level != "Log" && l.level != "Warning");
                if (!string.IsNullOrEmpty(contains)) lines = lines.Where(l => l.message != null && l.message.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0);
                var list = lines.ToList();
                return list.Count > limit ? list.Skip(list.Count - limit).ToList() : list;
            }
        }
    }
}
#endif
