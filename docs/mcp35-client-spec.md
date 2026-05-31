# Mcp35.Client — Implementation Spec (Phase 3)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`,
`mcp35-core-spec.md`, `mcp35-server-spec.md`; realizes **phase 3** of the
roadmap.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

`Mcp35.Client` is the reusable **client-side** half: the transport
implementations and `McpServerConnection` (one server: handshake, capability
negotiation, tools cache, call correlation, lifecycle). Phase 3 delivers the
**stdio** transport + connection and the **host wiring to list tools**;
`HttpTransport` is phase 8. It builds on `Mcp35.Core` and has a strict one-way
dependency (no `GxPT.*`, D8).

---

## 0. Scope & constraints

- **In scope (phase 3):** `StdioTransport` (spawns/owns a server child process),
  `McpServerConnection` (drives `initialize`→`initialized`, caches `tools/list`,
  routes `tools/call`), and the **host config plumbing** (mcp.json loader,
  built-in launch defs + toggles, `McpHost` assembly) culminating in *listing*
  the union of tools.
- **Out of scope:** the OpenRouter tool-call loop (phase 4), approval (phase 6),
  `HttpTransport`/GitHub (phase 8), multi-connection aggregation/ranking (host
  registry, D11).
- Same constraints: **net35**, no `async`/`Task`/`dynamic`, Newtonsoft only,
  **no `GxPT.*`**. curl path is injected (HTTP, phase 8).

---

## 1. Namespace & file layout

Assembly `Mcp35.Client`, root namespace `Mcp35.Client`. `ProjectRef → Mcp35.Core`.

```
src/Mcp35.Client/
├─ McpServerConnection.cs   handshake + state machine + tools cache + call routing
├─ ConnectionState.cs       enum + ConnectionStateEventArgs
├─ Transport/
│  ├─ StdioTransport.cs      IRpcTransport over a child process
│  ├─ StdioChannel.cs        IDuplexMessageChannel: process + reader/stderr threads
│  ├─ StdioLaunch.cs         command/args/cwd/env/grace
│  └─ HttpTransport.cs       (phase 8 — stub/placeholder in phase 3)
```

---

## 2. `StdioTransport` (`IRpcTransport` over a child process)

Composes a `StdioChannel` (process + framing) with a Core `JsonRpcPeer`
(id-correlation, pending table, inbound dispatch). The transport owns **process
lifecycle**; the peer owns **RPC semantics**.

```csharp
public sealed class StdioTransport : IRpcTransport   // Mcp35.Core.Rpc.IRpcTransport
{
    public StdioTransport(StdioLaunch launch, ILogSink log);

    void Start();                 // spawn process → build StdioChannel → JsonRpcPeer → channel.Start()
    bool IsConnected { get; }     // process alive && streams open
    JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs);  // → peer
    void SendNotification(string method, JToken @params);                        // → peer
    event EventHandler<JsonRpcInboundEventArgs> Inbound;                         // ← peer
    void Dispose();
}

public sealed class StdioLaunch {
    public string Command;                       // resolved exe path
    public string Arguments;
    public string WorkingDirectory;
    public IDictionary<string,string> Environment;
    public int    ShutdownGraceMs = 2000;        // EOF→exit grace before Kill
}
```

### `StdioChannel : IDuplexMessageChannel` (internal)
- `Start()` spawns via `ProcessStartInfo` (`UseShellExecute=false`,
  `CreateNoWindow=true`, all three streams redirected), exactly the pattern in
  `OpenRouterClient`.
- A **reader thread** loops `StdioFraming.ReadMessage(stdout)` → raises
  `MessageReceived` (one framed message). EOF / null → raise `Faulted` (process
  ended) and stop.
- A **stderr drain thread** reads server stderr → `ILogSink` (diagnostics only;
  never protocol).
- `Send(json)` → `StdioFraming.WriteMessage(stdin, json)` (UTF-8 + `\n` + flush),
  serialized under a write lock.
- UTF-8 via explicit `new UTF8Encoding(false, false)`.

### Shutdown (`Dispose`)
1. **Close stdin** → server sees EOF → its graceful `OnShutdown` runs (server
   spec §4).
2. `WaitForExit(ShutdownGraceMs)`; if still alive, **`Kill()`**.
3. Dispose the peer → **fault all pending calls** (`McpTransportException`) so no
   caller hangs; join threads.

### Unexpected exit
If the child dies on its own, `StdioChannel.Faulted` fires → the peer faults
pending calls and `StdioTransport` reports `IsConnected == false`;
`McpServerConnection` transitions to `Faulted` (§4).

---

## 3. `HttpTransport` (implemented — phase 8)

`IRpcTransport` implemented directly over Core's `CurlRunner` + `SseParser` (one
POST = one correlated response; no `JsonRpcPeer` needed). It was an unimplemented
stub in phase 3 so `McpServerConnection` could be written transport-agnostically;
phase 8 fills it in. See `mcp35-http-spec.md` for the full contract (session +
version headers, `Content-Type` branching, `DELETE` teardown, the HTTP version
floor enforced via `McpServerConnection.MinProtocolVersion`).

---

## 4. `McpServerConnection` — one connected server

Transport-agnostic (takes any `IRpcTransport`), so the same class serves stdio
now and HTTP later. Represents **exactly one** server (single-connection by
design, D11); the host owns the *collection*.

```csharp
public enum ConnectionState { Created, Starting, Initializing, Ready, Faulted, Closed }

public sealed class McpServerConnection : IDisposable
{
    public McpServerConnection(string name, IRpcTransport transport,
                               Implementation clientInfo, ILogSink log);

    public string           Name { get; }
    public ConnectionState  State { get; }
    public InitializeResult ServerInfo { get; }     // set once Ready
    public bool             SupportsTools { get; }  // capabilities.tools present

    public event EventHandler<ConnectionStateEventArgs> StateChanged;
    public event EventHandler                           ToolsChanged;

    public void           Open(int initializeTimeoutMs);          // Start + handshake → Ready
    public IList<Tool>    ListTools(bool refresh);                // cached; pages tools/list
    public CallToolResult CallTool(string name, JObject args, int timeoutMs);

    public void Close();      // graceful (transport Dispose → stdin EOF)
    public void Dispose();
}
```

### `Open` — the handshake (driven here, per Core spec §9)
1. `State = Starting`; `transport.Start()`.
2. `State = Initializing`; send **`initialize`** (`InitializeParams`:
   `protocolVersion = ProtocolVersions.Default`, **empty client capabilities**,
   `clientInfo`) and block for `InitializeResult` (timeout
   `initializeTimeoutMs` — generous; server startup can be slow).
3. **Validate the negotiated `protocolVersion`** (§5). Unacceptable → `Faulted`.
4. Store `ServerInfo`/capabilities; set `SupportsTools`.
5. Send **`notifications/initialized`**.
6. Prefetch `tools/list` into the cache (so the host can list immediately).
7. `State = Ready`.

### `ListTools(refresh)`
- Returns the **cached** list; on first call / `refresh` / after `ToolsChanged`,
  sends `tools/list`, following **`nextCursor`** pages until exhausted, and
  replaces the cache. Empty if `!SupportsTools`. Cache guarded by a lock.

### `CallTool(name, args, timeoutMs)`
- Requires `State == Ready`. Sends `tools/call`; returns the `CallToolResult`
  (including `isError` tool failures) **verbatim**. A JSON-RPC *error* response
  (e.g. unknown tool `-32602`) throws `McpException`. **No approval logic here**
  — that's the host (phase 6); the connection only routes.

### Notifications (via `transport.Inbound`, on the reader thread)
- `notifications/tools/list_changed` → invalidate the tools cache + raise
  `ToolsChanged` (the host re-lists). (Our first-party servers never send it —
  `listChanged=false` — but GitHub/custom may.)
- Other notifications (logging/progress) → log; ignored in phase 3.

### Lifecycle / faults
- Any transport fault or failed handshake → `State = Faulted`, `StateChanged`
  raised, pending calls already thrown by the peer. The host decides
  restart/disable policy (server-lifecycle work, §10).
- `Close`/`Dispose` → `transport.Dispose()` (graceful EOF) → `State = Closed`.

---

## 5. Capability negotiation & version acceptance

- Advertise `ProtocolVersions.Default`; the server echoes or downgrades.
- **Acceptance:** stdio accepts any returned version (framing is identical
  across revisions); HTTP (phase 8) requires `≥ HttpFloor` (`2025-03-26`).
  Unacceptable → `Faulted` with a clear message.
- **Tools gate:** `SupportsTools = ServerInfo.capabilities["tools"] != null`. If
  a server doesn't advertise tools, `ListTools` is empty and `CallTool` is
  rejected.
- Additive revision differences need no code (tolerant parsing, Core §2/§9).

---

## 6. Threading model

- The reader thread (in `StdioChannel`) drives `JsonRpcPeer` dispatch, so
  responses complete pending waits and `Inbound`/`ToolsChanged` fire **on the
  reader thread**. The host marshals to the UI as needed.
- `Open`/`ListTools`/`CallTool` **block the calling worker thread** (host calls
  them off the UI thread, like `CreateCompletionStream`).
- `JsonRpcPeer` correlates by id, so **multiple in-flight requests per
  connection are safe**; the tools cache is lock-guarded. Phase 3 host usage is
  effectively serial.

---

## 7. Host integration (phase 3, in GxPT `Services/Mcp/`)

This is the GxPT side that consumes the Client and lands the config plumbing
(architecture §8). Not part of the `Mcp35.*` library.

- **`McpConfig`** — loads `%AppData%/GxPT/mcp.json` (Newtonsoft `JObject`, per
  D16) → a list of server defs; infers transport (`url`→HTTP,
  `command`→stdio); applies the **GitHub PAT shape gate** (disable on malformed).
- **`BuiltInServers`** — hardcoded first-party launch defs (resolve
  `WebSearchMcpServer`/etc. under an app-relative `servers/` dir; inject the web search
  key into `env`), each gated by its `AppSettings` enable toggle.
- **`McpHost`** — assembles `McpServerConnection`s from **enabled built-ins** +
  **valid mcp.json entries**, builds the matching `StdioTransport`
  (`HttpTransport` in phase 8), `Open`s them, and exposes the connections to
  `McpToolRegistry`.
- **MCP settings tab** + toggles + web search key field land here too (architecture
  §8 UI).

**Phase-3 milestone:** the host can enumerate the **union of tools** across
connected stdio servers (via the registry) — proving Core + Server + Client +
config wire together end to end. *Calling* tools is phase 4.

---

## 8. Testing — phase-3 exit criteria

`Mcp35.Client.Tests` (net48 linked-source, pinned Newtonsoft):

1. **Connection over a loopback transport** — a fake `IRpcTransport` (no
   process) drives `Open`: asserts the `initialize`→`initialized` sequence,
   version validation, `SupportsTools`, and the `Created→…→Ready` state
   transitions.
2. **Tools cache + paging** — `ListTools` follows `nextCursor`, caches, and
   re-fetches after a `notifications/tools/list_changed` (`ToolsChanged` fires).
3. **CallTool routing** — `isError` results pass through; a `-32602` error
   response throws `McpException`.
4. **Fault paths** — handshake timeout → `Faulted`; mid-call transport fault →
   pending `McpTransportException` + `Faulted`.
5. **End-to-end over `StdioTransport`** — launch a **minimal `Mcp35.Server`-based
   echo server** and run `initialize` + `tools/list` + `tools/call` for real,
   then verify graceful shutdown on `Dispose` (stdin EOF → exit). This is the
   first Core+Server+Client integration test.

Passing this unblocks phase 4 (the tool-call loop).

---

## 9. Server-lifecycle decisions (resolved)

All host policy (the Client library stays mechanism-only); these set the
defaults the `McpHost` implements.

- **Connection open timing** — *resolved*: **lazy open + warm keepalive**. A
  server isn't spawned/handshaken until the catalog is first needed (the model
  is offered tools / a call resolves to it); once open it **stays open** across
  turns (no per-turn re-handshake) until the chat closes or it faults. Avoids
  paying cold-start for servers a given chat never uses, without re-init churn.
- **Restart/disable on fault** — *resolved*: **auto-restart a crashed stdio
  server up to 2× per session**, then **mark it disabled and surface that to the
  user** (a status indicator), rather than retrying forever. An HTTP server that
  faults is marked disabled immediately (re-enable is user-driven). A disabled
  server drops out of the catalog so the model stops being offered its tools.
- **Startup / call timeout defaults** — *resolved*: `initializeTimeoutMs`
  **10 s** (stdio cold-start headroom), default `tools/call` timeout **60 s**;
  both named constants, **tunable** without a design change. HTTP handshakes use
  the same init timeout.
- **Inbound server→client requests** — *resolved*: handled by Core's
  `JsonRpcPeer` (auto-`ping`, `-32601` otherwise); the connection just logs them
  (no sampling/roots/elicitation in scope).
