using System;
using System.Collections.Generic;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;

namespace GxPT
{
    // Owns the host's MCP server connections (D11) and the shared McpToolRegistry. Assembles
    // connections from config specs, wires each connection's lifecycle into the registry, and
    // manages the lifecycle of workdir-scoped servers:
    //   * workdir-independent servers (web + every mcp.json entry) are opened once via Start();
    //   * workdir-scoped built-ins (files/git/command) run as ONE process set PER working directory.
    //     EnsureWorkingDir(dir) lazily launches a folder's set (GXPT_WORKDIR=dir) and keeps it alive;
    //     several conversation tabs sharing a folder share its set, while tabs on different folders get
    //     independent sets. Switching tabs never tears anything down — only ReleaseWorkingDir/RetainOnly
    //     (driven by which folders still have an open tab) and Dispose close scoped servers.
    // Each scoped connection is registered with its workdir so tool calls resolve to the folder that
    // requested them (McpToolRegistry.TryResolve(name, workdir)).
    // Transport construction is delegated to an IServerConnector so this logic is testable without
    // spawning processes. Thread-safe via a single lock; event handlers touch only the (separately
    // locked) registry, so they never re-enter this lock.
    internal sealed class McpHost : IDisposable
    {
        public const int DefaultOpenTimeoutMs = 15000;

        private readonly IServerConnector _connector;
        private readonly McpToolRegistry _registry;
        private readonly ILogSink _log;
        private readonly int _openTimeoutMs;
        private readonly object _lock = new object();

        private readonly List<McpServerConnection> _eager = new List<McpServerConnection>();
        // One scoped connection set per working directory (key = the directory as supplied).
        private readonly Dictionary<string, List<McpServerConnection>> _scopedByWorkdir =
            new Dictionary<string, List<McpServerConnection>>(StringComparer.OrdinalIgnoreCase);
        // Working dirs requested before Start() knew the scoped specs; launched when Start arrives.
        private readonly List<string> _pendingWorkdirs = new List<string>();
        private List<McpServerSpec> _scopedSpecs = new List<McpServerSpec>();
        private bool _started;
        private bool _disposed;

        public McpHost(IServerConnector connector, McpToolRegistry registry, ILogSink log)
            : this(connector, registry, log, DefaultOpenTimeoutMs)
        {
        }

        public McpHost(IServerConnector connector, McpToolRegistry registry, ILogSink log, int openTimeoutMs)
        {
            if (connector == null) throw new ArgumentNullException("connector");
            if (registry == null) throw new ArgumentNullException("registry");
            _connector = connector;
            _registry = registry;
            _log = log != null ? log : NullLogSink.Instance;
            _openTimeoutMs = openTimeoutMs > 0 ? openTimeoutMs : DefaultOpenTimeoutMs;
        }

        public McpToolRegistry Registry { get { return _registry; } }

        // The working directories that currently have a live (or pending) scoped server set. Snapshot;
        // safe to enumerate.
        public string[] ActiveWorkingDirs
        {
            get
            {
                lock (_lock)
                {
                    var keys = new List<string>(_scopedByWorkdir.Keys);
                    for (int i = 0; i < _pendingWorkdirs.Count; i++)
                        if (!keys.Contains(_pendingWorkdirs[i])) keys.Add(_pendingWorkdirs[i]);
                    return keys.ToArray();
                }
            }
        }

        // Open all enabled, workdir-independent servers; remember the workdir-scoped specs for
        // EnsureWorkingDir. Call once after building specs from config.
        public void Start(IEnumerable<McpServerSpec> specs)
        {
            lock (_lock)
            {
                if (_disposed) return;
                List<McpServerSpec> scoped = new List<McpServerSpec>();
                if (specs != null)
                {
                    foreach (McpServerSpec spec in specs)
                    {
                        if (spec == null) continue;
                        if (spec.WorkdirScoped) { scoped.Add(spec); continue; }
                        if (!spec.Enabled) continue;
                        McpServerConnection conn = ConnectAndAdd(spec, null);
                        if (conn != null) _eager.Add(conn);
                    }
                }
                _scopedSpecs = scoped;
                _started = true;

                // Launch any working directories requested before Start knew the scoped specs.
                if (_pendingWorkdirs.Count > 0)
                {
                    List<string> pending = new List<string>(_pendingWorkdirs);
                    _pendingWorkdirs.Clear();
                    for (int i = 0; i < pending.Count; i++) OpenScopedLocked(pending[i]);
                }
            }
        }

        // Ensure the workdir-scoped servers (files/git/command) for `workdir` are running. Idempotent:
        // a folder already served returns immediately; other folders' sets are left untouched. A
        // null/empty workdir is a no-op (no scoped tools for a folderless conversation). Safe to call
        // from a worker thread right before a tool turn; (re)connecting can block.
        public void EnsureWorkingDir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;
            lock (_lock)
            {
                if (_disposed) return;
                if (_scopedByWorkdir.ContainsKey(workdir)) return;
                if (!_started)
                {
                    if (!_pendingWorkdirs.Contains(workdir)) _pendingWorkdirs.Add(workdir);
                    return;
                }
                OpenScopedLocked(workdir);
            }
        }

        // Tear down the scoped servers for a single working directory (e.g. its last tab closed).
        public void ReleaseWorkingDir(string workdir)
        {
            if (string.IsNullOrEmpty(workdir)) return;
            lock (_lock)
            {
                _pendingWorkdirs.Remove(workdir);
                List<McpServerConnection> conns;
                if (_scopedByWorkdir.TryGetValue(workdir, out conns))
                {
                    for (int i = 0; i < conns.Count; i++) Teardown(conns[i]);
                    _scopedByWorkdir.Remove(workdir);
                }
            }
        }

        // Keep only the scoped server sets whose working directory is still in `keep`; tear down the
        // rest. Called when the set of open tabs (and thus referenced folders) changes, so processes
        // for closed conversations don't linger.
        public void RetainOnly(IEnumerable<string> keep)
        {
            var keepSet = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (keep != null)
                foreach (string k in keep)
                    if (!string.IsNullOrEmpty(k)) keepSet[k] = true;

            lock (_lock)
            {
                if (_disposed) return;
                for (int i = _pendingWorkdirs.Count - 1; i >= 0; i--)
                    if (!keepSet.ContainsKey(_pendingWorkdirs[i])) _pendingWorkdirs.RemoveAt(i);

                List<string> drop = null;
                foreach (string wd in _scopedByWorkdir.Keys)
                    if (!keepSet.ContainsKey(wd)) { (drop ?? (drop = new List<string>())).Add(wd); }
                if (drop != null)
                {
                    for (int i = 0; i < drop.Count; i++)
                    {
                        List<McpServerConnection> conns = _scopedByWorkdir[drop[i]];
                        for (int j = 0; j < conns.Count; j++) Teardown(conns[j]);
                        _scopedByWorkdir.Remove(drop[i]);
                    }
                }
            }
        }

        // Launch all enabled scoped specs for `workdir` and record the set. Caller holds _lock.
        private void OpenScopedLocked(string workdir)
        {
            if (string.IsNullOrEmpty(workdir) || _scopedByWorkdir.ContainsKey(workdir)) return;
            List<McpServerConnection> conns = new List<McpServerConnection>();
            for (int i = 0; i < _scopedSpecs.Count; i++)
            {
                McpServerSpec spec = _scopedSpecs[i];
                if (spec == null || !spec.Enabled) continue;
                McpServerConnection conn = ConnectAndAdd(spec, workdir);
                if (conn != null) conns.Add(conn);
            }
            _scopedByWorkdir[workdir] = conns;
        }

        private McpServerConnection ConnectAndAdd(McpServerSpec spec, string workdir)
        {
            McpServerConnection conn;
            try { conn = _connector.Create(spec, workdir); }
            catch (Exception ex)
            {
                _log.Log("mcp", "connector failed for '" + spec.Name + "': " + ex.Message);
                return null;
            }
            if (conn == null) return null;

            // Wire lifecycle → registry before Open so a fault during/after open is handled.
            conn.ToolsChanged += OnToolsChanged;
            conn.StateChanged += OnStateChanged;

            try
            {
                if (conn.State == ConnectionState.Created) conn.Open(_openTimeoutMs);
            }
            catch (Exception ex)
            {
                _log.Log("mcp", "open failed for '" + spec.Name + "': " + ex.Message);
                Teardown(conn);
                return null;
            }

            if (conn.State == ConnectionState.Ready)
            {
                // Tag the connection with its workdir so tool calls resolve to the right folder.
                _registry.AddConnection(conn, workdir);
                return conn;
            }

            _log.Log("mcp", "server '" + spec.Name + "' not Ready after open (state=" + conn.State + ").");
            Teardown(conn);
            return null;
        }

        private void OnToolsChanged(object sender, EventArgs e)
        {
            McpServerConnection conn = sender as McpServerConnection;
            if (conn != null) _registry.RefreshConnection(conn);
        }

        private void OnStateChanged(object sender, ConnectionStateEventArgs e)
        {
            if (e == null) return;
            if (e.NewState == ConnectionState.Faulted || e.NewState == ConnectionState.Closed)
            {
                McpServerConnection conn = sender as McpServerConnection;
                if (conn != null) _registry.RemoveConnection(conn);
            }
        }

        // Unsubscribe (so Dispose's Closed event doesn't re-enter), drop from the registry, dispose.
        private void Teardown(McpServerConnection conn)
        {
            if (conn == null) return;
            try { conn.ToolsChanged -= OnToolsChanged; }
            catch { }
            try { conn.StateChanged -= OnStateChanged; }
            catch { }
            _registry.RemoveConnection(conn);
            try { conn.Dispose(); }
            catch { }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (List<McpServerConnection> conns in _scopedByWorkdir.Values)
                    for (int i = 0; i < conns.Count; i++) Teardown(conns[i]);
                _scopedByWorkdir.Clear();
                _pendingWorkdirs.Clear();
                for (int i = 0; i < _eager.Count; i++) Teardown(_eager[i]);
                _eager.Clear();
            }
        }
    }
}
