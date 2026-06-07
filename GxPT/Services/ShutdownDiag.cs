using System.Diagnostics;

namespace GxPT
{
    // TEMPORARY shutdown-latency instrumentation. Times the app-close path so we can see where the
    // 1-2s close delay actually goes (SaveOpenTabs vs MCP teardown vs control-tree/transcript
    // disposal vs post-Dispose finalizers). All output goes through Logger, so it only writes when
    // logging is enabled (GXPT_LOG=1 or the enable_logging setting). Remove once diagnosed.
    internal static class ShutdownDiag
    {
        private static readonly Stopwatch _sw = new Stopwatch();

        // Start the close clock (called at the top of FormClosing). Idempotent.
        public static void Begin()
        {
            if (!_sw.IsRunning) _sw.Start();
        }

        // True once Begin() has run — used to ignore transcript disposals during normal runtime.
        public static bool Running { get { return _sw.IsRunning; } }

        // Milliseconds since Begin().
        public static long Now { get { return _sw.IsRunning ? _sw.ElapsedMilliseconds : 0; } }

        public static void Log(string msg)
        {
            try { Logger.Log("shutdown", "[" + Now + "ms] " + msg); }
            catch { }
        }

        // Aggregate accounting for ChatTranscriptControl disposal during the close window.
        public static int TranscriptCount;
        public static long TranscriptTotalMs;
    }
}
