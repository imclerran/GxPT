using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace GxPT
{
    // TEMPORARY shutdown-latency instrumentation. Times the app-close path so we can see where the
    // 1-2s close delay actually goes. Marks are buffered in memory (near-zero cost) and written to
    // the log in a SINGLE batch at Flush(), so the logger's own file I/O (~50ms/call on XP) does
    // not distort the measurements. Output is Logger-gated (GXPT_LOG=1 / enable_logging). Remove
    // once diagnosed.
    internal static class ShutdownDiag
    {
        private static readonly Stopwatch _sw = new Stopwatch();
        private static readonly List<string> _marks = new List<string>();
        private static readonly object _gate = new object();
        private static int _flushedUpTo; // index of first not-yet-written mark
        private static bool _enabled;    // snapshot of the logging gate, taken at Begin()

        // Start the close clock (called at the top of FormClosing). Idempotent. When logging is off,
        // the whole diagnostic stays dormant so normal shutdowns are completely unaffected.
        public static void Begin()
        {
            if (_sw.IsRunning) return;
            _enabled = Logger.Enabled;
            if (_enabled) _sw.Start();
        }

        // True only while a measured (logging-on) close is in progress.
        public static bool Running { get { return _enabled && _sw.IsRunning; } }

        // Whether this close is being measured (used to gate the heavier probes, e.g. the forced GC).
        public static bool Enabled { get { return _enabled; } }

        // Milliseconds since Begin().
        public static long Now { get { return _sw.IsRunning ? _sw.ElapsedMilliseconds : 0; } }

        // Record a timeline mark. Cheap: just appends to an in-memory list. No-op when not measuring.
        public static void Mark(string label)
        {
            if (!_enabled) return;
            lock (_gate) { _marks.Add("[" + Now + "ms] " + label); }
        }

        // Write any marks recorded since the last Flush, in one batched Logger call. Incremental, so
        // calling it after Application.Run returns AND again at ProcessExit captures the full
        // timeline (including the finalizer/CLR-shutdown tail) without dropping or duplicating marks.
        public static void Flush()
        {
            string payload;
            lock (_gate)
            {
                if (_flushedUpTo >= _marks.Count) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("close timeline:");
                for (int i = _flushedUpTo; i < _marks.Count; i++) sb.Append("\n    ").Append(_marks[i]);
                _flushedUpTo = _marks.Count;
                payload = sb.ToString();
            }
            try { Logger.Log("shutdown", payload); }
            catch { }
        }

        // Aggregate accounting for ChatTranscriptControl disposal during the close window.
        public static int TranscriptCount;
        public static long TranscriptTotalMs;
    }
}
