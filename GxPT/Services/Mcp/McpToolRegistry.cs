using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Mcp35.Client;
using Mcp35.Core.Diagnostics;
using Mcp35.Core.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GxPT
{
    // The host's tool-discovery component (phase 5). Aggregates every connected server's tools into
    // (a) a cheap names manifest, (b) a small LRU-capped set of "revealed" full definitions, and
    // (c) reveal_tools, the meta-tool that moves a tool from "known by name" to "callable". Owns the
    // server-qualified function-name bijection so tool_calls resolve back to (connection, toolName).
    //
    // Thread-safe: lifecycle methods are driven from connection reader threads (Ready/ToolsChanged/
    // fault), while the orchestrator calls the per-request/resolution methods from its worker thread.
    internal sealed class McpToolRegistry
    {
        public const string RevealToolsName = "reveal_tools";
        public const int DefaultRevealCap = 24; // discovery spec §13

        private sealed class CatalogEntry
        {
            public McpServerConnection Conn;
            public string OriginalName;
            public string FunctionName;
            public string Description;
            public JObject Schema;
        }

        private readonly object _lock = new object();
        private readonly Dictionary<string, CatalogEntry> _byFunctionName =
            new Dictionary<string, CatalogEntry>(StringComparer.Ordinal);
        private readonly Dictionary<McpServerConnection, List<string>> _byConnection =
            new Dictionary<McpServerConnection, List<string>>();
        private readonly Dictionary<string, long> _revealed = new Dictionary<string, long>(StringComparer.Ordinal);

        private readonly int _revealCap;
        private readonly ILogSink _log;
        private long _tick;
        private string _manifestCache;
        private bool _manifestDirty = true;

        public McpToolRegistry(int revealCap, ILogSink log)
        {
            _revealCap = revealCap > 0 ? revealCap : DefaultRevealCap;
            _log = log != null ? log : NullLogSink.Instance;
        }

        // ---- lifecycle (driven by McpHost from connection events) ----

        public void AddConnection(McpServerConnection conn)
        {
            if (conn == null) return;
            // ListTools may hit the transport — call it outside the lock, then merge under it.
            IList<Tool> tools = SafeListTools(conn, false);

            lock (_lock)
            {
                List<string> fns;
                if (!_byConnection.TryGetValue(conn, out fns))
                {
                    fns = new List<string>();
                    _byConnection[conn] = fns;
                }
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool t = tools[i];
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    string fn = Munge(conn.Name, t.Name);

                    CatalogEntry e = new CatalogEntry();
                    e.Conn = conn;
                    e.OriginalName = t.Name;
                    e.FunctionName = fn;
                    e.Description = t.Description;
                    e.Schema = t.InputSchema;
                    _byFunctionName[fn] = e;
                    if (!fns.Contains(fn)) fns.Add(fn);
                }
                _manifestDirty = true;
            }
        }

        public void RefreshConnection(McpServerConnection conn)
        {
            if (conn == null) return;
            IList<Tool> tools = SafeListTools(conn, true);

            lock (_lock)
            {
                RemoveConnectionLocked(conn);

                List<string> fns = new List<string>();
                _byConnection[conn] = fns;
                for (int i = 0; i < tools.Count; i++)
                {
                    Tool t = tools[i];
                    if (t == null || string.IsNullOrEmpty(t.Name)) continue;
                    string fn = Munge(conn.Name, t.Name);

                    CatalogEntry e = new CatalogEntry();
                    e.Conn = conn;
                    e.OriginalName = t.Name;
                    e.FunctionName = fn;
                    e.Description = t.Description;
                    e.Schema = t.InputSchema;
                    _byFunctionName[fn] = e;
                    if (!fns.Contains(fn)) fns.Add(fn);
                }
                _manifestDirty = true;
            }
        }

        public void RemoveConnection(McpServerConnection conn)
        {
            if (conn == null) return;
            lock (_lock)
            {
                RemoveConnectionLocked(conn);
                _manifestDirty = true;
            }
        }

        private void RemoveConnectionLocked(McpServerConnection conn)
        {
            List<string> fns;
            if (_byConnection.TryGetValue(conn, out fns))
            {
                for (int i = 0; i < fns.Count; i++)
                {
                    _byFunctionName.Remove(fns[i]);
                    _revealed.Remove(fns[i]);
                }
                _byConnection.Remove(conn);
            }
        }

        // ---- per-request (from the orchestrator) ----

        public string NamesManifestSystemMessage()
        {
            lock (_lock)
            {
                if (_manifestDirty || _manifestCache == null)
                {
                    List<string> names = new List<string>(_byFunctionName.Keys);
                    names.Sort(StringComparer.Ordinal); // server__ prefix groups visually

                    StringBuilder sb = new StringBuilder();
                    sb.Append("Available MCP tools. Call reveal_tools({\"names\":[...]}) to load a tool's ");
                    sb.Append("full definition before you can call it.");
                    for (int i = 0; i < names.Count; i++)
                        sb.Append("\n- ").Append(names[i]);
                    _manifestCache = sb.ToString();
                    _manifestDirty = false;
                }
                return _manifestCache;
            }
        }

        public IList<JObject> ExposedFunctionDefs()
        {
            List<JObject> result = new List<JObject>();
            result.Add(RevealToolsDef());
            lock (_lock)
            {
                // Order the revealed set by recency (oldest first) for a stable, deterministic array.
                List<string> fns = new List<string>(_revealed.Keys);
                fns.Sort(delegate(string a, string b) { return _revealed[a].CompareTo(_revealed[b]); });
                for (int i = 0; i < fns.Count; i++)
                {
                    CatalogEntry e;
                    if (_byFunctionName.TryGetValue(fns[i], out e))
                        result.Add(FunctionDef(e.FunctionName, e.Description, CloneSchema(e.Schema)));
                }
            }
            return result;
        }

        // ---- tool_call handling ----

        public bool IsRevealTools(string functionName)
        {
            return functionName == RevealToolsName;
        }

        public string Reveal(string[] names)
        {
            JArray defs = new JArray();
            List<string> notes = new List<string>();

            lock (_lock)
            {
                if (names != null)
                {
                    for (int i = 0; i < names.Length; i++)
                    {
                        string n = names[i];
                        CatalogEntry e;
                        if (n != null && _byFunctionName.TryGetValue(n, out e))
                        {
                            _revealed[n] = ++_tick; // add or bump recency
                            JObject d = new JObject();
                            d["name"] = e.FunctionName;
                            d["description"] = e.Description != null ? e.Description : string.Empty;
                            d["parameters"] = CloneSchema(e.Schema);
                            defs.Add(d);
                        }
                        else
                        {
                            notes.Add("unknown tool: " + (n != null ? n : "(null)"));
                        }
                    }
                }
                EnforceCapLocked();
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(defs.ToString(Formatting.None));
            for (int i = 0; i < notes.Count; i++)
                sb.Append("\n").Append(notes[i]);
            return sb.ToString();
        }

        public bool TryResolve(string functionName, out McpServerConnection conn, out string toolName)
        {
            lock (_lock)
            {
                CatalogEntry e;
                if (functionName != null && _byFunctionName.TryGetValue(functionName, out e))
                {
                    if (_revealed.ContainsKey(functionName))
                        _revealed[functionName] = ++_tick; // keep an actively-called tool alive
                    conn = e.Conn;
                    toolName = e.OriginalName;
                    return true;
                }
            }
            conn = null;
            toolName = null;
            return false;
        }

        // ---- internals ----

        private void EnforceCapLocked()
        {
            while (_revealed.Count > _revealCap)
            {
                string victim = null;
                long min = long.MaxValue;
                foreach (KeyValuePair<string, long> kv in _revealed)
                {
                    if (kv.Value < min) { min = kv.Value; victim = kv.Key; }
                }
                if (victim == null) break;
                _revealed.Remove(victim);
            }
        }

        private static JObject RevealToolsDef()
        {
            JObject props = new JObject();
            JObject namesProp = new JObject();
            namesProp["type"] = "array";
            JObject items = new JObject();
            items["type"] = "string";
            namesProp["items"] = items;
            props["names"] = namesProp;

            JObject schema = new JObject();
            schema["type"] = "object";
            schema["properties"] = props;
            schema["required"] = new JArray("names");

            return FunctionDef(RevealToolsName,
                "Load full definitions for tools by exact name so they become callable.", schema);
        }

        // Clone so the catalog's stored schema is never reparented into a request-body JObject
        // (a JToken can have only one parent; exposed defs are rebuilt every request).
        private static JObject CloneSchema(JObject schema)
        {
            return schema != null ? (JObject)schema.DeepClone() : new JObject();
        }

        private static JObject FunctionDef(string name, string description, JObject parameters)
        {
            JObject fn = new JObject();
            fn["name"] = name;
            fn["description"] = description != null ? description : string.Empty;
            fn["parameters"] = parameters != null ? (JToken)parameters : new JObject();

            JObject def = new JObject();
            def["type"] = "function";
            def["function"] = fn;
            return def;
        }

        // Deterministic, OpenAI-legal, server-qualified name. Collisions/over-length resolved by a
        // stable hash suffix. Bound (not yet) here — caller binds the returned name to its entry.
        private string Munge(string serverName, string toolName)
        {
            string raw = (serverName != null ? serverName : string.Empty) + "__" +
                         (toolName != null ? toolName : string.Empty);
            string baseName = Sanitize(raw);

            if (baseName.Length <= 64 && !CollidesWithDifferentLocked(baseName, serverName, toolName))
                return baseName;

            string h = ShortHash((serverName != null ? serverName : string.Empty) + "\0" +
                                 (toolName != null ? toolName : string.Empty));
            return Truncate(baseName, 64 - 7) + "_" + h;
        }

        private bool CollidesWithDifferentLocked(string baseName, string serverName, string toolName)
        {
            CatalogEntry e;
            if (!_byFunctionName.TryGetValue(baseName, out e)) return false;
            // Same identity (e.g. a stale entry being re-added) is not a collision.
            bool sameServer = (e.Conn != null && e.Conn.Name == serverName);
            return !(sameServer && e.OriginalName == toolName);
        }

        private static string Sanitize(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') ||
                          (c >= '0' && c <= '9') || c == '_' || c == '-';
                sb.Append(ok ? c : '_');
            }
            return sb.ToString();
        }

        private static string Truncate(string s, int n)
        {
            if (s.Length <= n) return s;
            return s.Substring(0, n);
        }

        // FNV-1a over UTF-8 bytes → 6 lowercase hex. Deterministic across runs (no crypto dep).
        private static string ShortHash(string s)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s);
            uint h = 2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                h ^= bytes[i];
                h *= 16777619u;
            }
            return h.ToString("x8", CultureInfo.InvariantCulture).Substring(0, 6);
        }
    }
}
