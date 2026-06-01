using System;
using System.Collections.Generic;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;

namespace GxPT
{
    // Owns the host's MCP server connections (D11) and the shared McpToolRegistry. Assembles
    // connections from config specs, wires each connection's lifecycle into the registry, and
    // manages the lazy, per-conversation lifecycle of workdir-scoped servers:
    //   * workdir-independent servers (web + every mcp.json entry) are opened once via Start();
    //   * workdir-scoped built-ins (files/git/command) are (re)opened by SetActiveWorkingDir with
    //     GXPT_WORKDIR set to the active conversation's directory, and torn down when it changes.
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
        private readonly List<McpServerConnection> _scoped = new List<McpServerConnection>();
        private List<McpServerSpec> _scopedSpecs = new List<McpServerSpec>();
        private string _currentWorkdir;
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

        public string ActiveWorkingDir { get { lock (_lock) { return _currentWorkdir; } } }

        // Open all enabled, workdir-independent servers; remember the workdir-scoped specs for
        // SetActiveWorkingDir. Call once after building specs from config.
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

                // If a working directory was already set (e.g. SetActiveWorkingDir raced ahead of
                // Start on startup), launch the scoped servers for it now.
                if (_currentWorkdir != null && _scoped.Count == 0)
                {
                    for (int i = 0; i < _scopedSpecs.Count; i++)
                    {
                        McpServerSpec spec = _scopedSpecs[i];
                        if (spec == null || !spec.Enabled) continue;
                        McpServerConnection conn = ConnectAndAdd(spec, _currentWorkdir);
                        if (conn != null) _scoped.Add(conn);
                    }
                }
            }
        }

        // Make `workdir` the active conversation's directory: tear down the previous workdir-scoped
        // servers and open fresh ones bound to it. null/empty disconnects them (no scoped tools).
        public void SetActiveWorkingDir(string workdir)
        {
            string wd = string.IsNullOrEmpty(workdir) ? null : workdir;
            lock (_lock)
            {
                if (_disposed) return;
                if (_currentWorkdir == wd) return;

                for (int i = 0; i < _scoped.Count; i++) Teardown(_scoped[i]);
                _scoped.Clear();
                _currentWorkdir = wd;

                if (wd != null)
                {
                    for (int i = 0; i < _scopedSpecs.Count; i++)
                    {
                        McpServerSpec spec = _scopedSpecs[i];
                        if (spec == null || !spec.Enabled) continue;
                        McpServerConnection conn = ConnectAndAdd(spec, wd);
                        if (conn != null) _scoped.Add(conn);
                    }
                }
            }
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
                _registry.AddConnection(conn);
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
                for (int i = 0; i < _scoped.Count; i++) Teardown(_scoped[i]);
                for (int i = 0; i < _eager.Count; i++) Teardown(_eager[i]);
                _scoped.Clear();
                _eager.Clear();
            }
        }
    }
}
