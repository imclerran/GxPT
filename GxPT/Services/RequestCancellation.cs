using System.Diagnostics;

namespace GxPT
{
    // A per-request cancellation handle for an in-flight model stream. The streaming code runs curl
    // via a Process and blocks reading its stdout; cancellation works by killing that process, which
    // drops the HTTP connection so the API stops generating. Created fresh per send and stored on the
    // tab context, so cancelling one tab never touches another tab's concurrent stream.
    //
    // Threading: Cancel() runs on the UI thread (the Stop button); Attach/Detach run on the streaming
    // worker thread. A lock coordinates the two, and the cancelled flag is honored even when Cancel
    // races ahead of Attach - the process is killed as soon as it is registered.
    internal sealed class RequestCancellation
    {
        private readonly object _gate = new object();
        private Process _proc;
        private volatile bool _cancelled;

        // True once Cancel() has been called. The streaming and orchestrator loops read this to bail
        // out cleanly, treating the stop as a user action rather than an error.
        public bool IsCancelled { get { return _cancelled; } }

        // Register the live curl process so a (possibly already-requested) Cancel can kill it. If
        // Cancel arrived first, the process is killed immediately on registration.
        public void Attach(Process proc)
        {
            lock (_gate)
            {
                _proc = proc;
                if (_cancelled) TryKill(proc);
            }
        }

        // Forget the process once it has exited normally, so a late Cancel can't kill an unrelated
        // process that happened to reuse its PID.
        public void Detach()
        {
            lock (_gate) { _proc = null; }
        }

        // Request cancellation: latch the flag and kill the in-flight process if one is registered.
        public void Cancel()
        {
            Process p;
            lock (_gate)
            {
                _cancelled = true;
                p = _proc;
            }
            if (p != null) TryKill(p);
        }

        private static void TryKill(Process p)
        {
            try { if (!p.HasExited) p.Kill(); }
            catch { }
        }
    }
}
