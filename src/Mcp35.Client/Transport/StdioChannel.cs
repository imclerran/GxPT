using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Rpc;
using Mcp35.Core.Transport;

namespace Mcp35.Client.Transport
{
    /// <summary>
    /// An <see cref="IDuplexMessageChannel"/> backed by a child process: spawns it, reads framed
    /// messages off stdout on a reader thread, drains stderr to the log, and writes framed
    /// messages to stdin under a write lock. See mcp35-client-spec.md §2.
    /// </summary>
    internal sealed class StdioChannel : IDuplexMessageChannel
    {
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

        private readonly StdioLaunch _launch;
        private readonly ILogSink _log;
        private readonly object _writeLock = new object();

        private System.Diagnostics.Process _proc;
        private StreamReader _stdoutReader;
        private Thread _readerThread;
        private Thread _stderrThread;

        private volatile bool _started;
        private volatile bool _stopping;
        private int _faultRaised; // 0/1 guard so Faulted fires at most once

        /// <summary>
        /// When set before <see cref="Dispose"/>, the child is killed immediately instead of being
        /// given the stdin-EOF grace window. Kill (TerminateProcess) guarantees the child dies even
        /// though we don't wait, and the reader/stderr threads are background threads, so this path
        /// neither waits for exit nor joins them — teardown is effectively instantaneous. Used for
        /// application/host shutdown where speed matters more than a polite handshake.
        /// </summary>
        internal bool ForcefulShutdown;

        public event Action<string> MessageReceived;
        public event Action<Exception> Faulted;

        public StdioChannel(StdioLaunch launch, ILogSink log)
        {
            if (launch == null) throw new ArgumentNullException("launch");
            if (string.IsNullOrEmpty(launch.Command)) throw new ArgumentException("Command is required.", "launch");
            _launch = launch;
            _log = log ?? NullLogSink.Instance;
        }

        public bool IsAlive
        {
            get
            {
                if (!_started || _proc == null) return false;
                try { return !_proc.HasExited; }
                catch { return false; }
            }
        }

        public void Start()
        {
            if (_started) return;

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = _launch.Command;
            psi.Arguments = _launch.Arguments ?? string.Empty;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.StandardOutputEncoding = Utf8;
            psi.StandardErrorEncoding = Utf8;
            if (!string.IsNullOrEmpty(_launch.WorkingDirectory)) psi.WorkingDirectory = _launch.WorkingDirectory;
            if (_launch.Environment != null)
            {
                foreach (System.Collections.Generic.KeyValuePair<string, string> kv in _launch.Environment)
                    psi.EnvironmentVariables[kv.Key] = kv.Value;
            }

            _proc = new System.Diagnostics.Process();
            _proc.StartInfo = psi;
            _proc.Start();
            _started = true;

            _stdoutReader = _proc.StandardOutput;

            _readerThread = new Thread(ReaderLoop);
            _readerThread.IsBackground = true;
            _readerThread.Name = "mcp-stdio-reader";
            _readerThread.Start();

            _stderrThread = new Thread(StderrLoop);
            _stderrThread.IsBackground = true;
            _stderrThread.Name = "mcp-stdio-stderr";
            _stderrThread.Start();
        }

        public void Send(string json)
        {
            if (!_started) throw new InvalidOperationException("Channel not started.");
            lock (_writeLock)
            {
                Stream stdin = _proc.StandardInput.BaseStream;
                StdioFraming.WriteMessage(stdin, json);
            }
        }

        private void ReaderLoop()
        {
            try
            {
                string line;
                while ((line = StdioFraming.ReadMessage(_stdoutReader)) != null)
                {
                    Action<string> h = MessageReceived;
                    if (h != null) h(line);
                }
                // null => EOF: the process closed stdout (normal on shutdown, or it died).
                if (!_stopping) RaiseFault(new EndOfStreamException("Server stdout closed (process exited)."));
            }
            catch (Exception ex)
            {
                if (!_stopping) RaiseFault(ex);
            }
        }

        private void StderrLoop()
        {
            try
            {
                StreamReader err = _proc.StandardError;
                string line;
                while ((line = err.ReadLine()) != null)
                {
                    _log.Log("mcp-server", line);
                }
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "stderr drain ended: " + ex.Message);
            }
        }

        private void RaiseFault(Exception ex)
        {
            if (Interlocked.CompareExchange(ref _faultRaised, 1, 0) != 0) return;
            Action<Exception> h = Faulted;
            if (h != null) h(ex);
        }

        public void Dispose()
        {
            _stopping = true;

            // 1. Close stdin → server sees EOF → graceful shutdown.
            try
            {
                if (_proc != null) _proc.StandardInput.Close();
            }
            catch (Exception ex) { _log.Log("mcp", "Closing stdin threw: " + ex.Message); }

            // Forceful (app/host shutdown): kill now instead of waiting out the stdin-EOF grace.
            // Killing closes the child's pipes, so the reader/stderr threads hit clean EOF and exit
            // within ~1ms — we join them (near-instant here, unlike the graceful wait on a still-live
            // process) so the streams are never disposed out from under a live read. That keeps this
            // path exception-free while still completing in milliseconds. _stopping is already set,
            // so the threads' EOF is treated as an intentional stop, not a fault.
            if (ForcefulShutdown)
            {
                try { if (_proc != null && !_proc.HasExited) _proc.Kill(); }
                catch (Exception ex) { _log.Log("mcp", "Kill threw: " + ex.Message); }
                JoinThread(_readerThread);
                JoinThread(_stderrThread);
                try { if (_proc != null) _proc.Close(); }
                catch { }
                return;
            }

            // 2. Wait for graceful exit, else kill.
            try
            {
                if (_proc != null)
                {
                    if (!_proc.WaitForExit(_launch.ShutdownGraceMs))
                    {
                        try { if (!_proc.HasExited) _proc.Kill(); }
                        catch (Exception ex) { _log.Log("mcp", "Kill threw: " + ex.Message); }
                    }
                }
            }
            catch (Exception ex) { _log.Log("mcp", "WaitForExit threw: " + ex.Message); }

            // 3. Join threads.
            JoinThread(_readerThread);
            JoinThread(_stderrThread);

            try { if (_proc != null) _proc.Close(); }
            catch { }
        }

        private static void JoinThread(Thread t)
        {
            if (t != null && t.IsAlive) t.Join(2000);
        }
    }
}
