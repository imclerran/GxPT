# Mcp35.Server — Implementation Spec (Phase 2)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`
and `mcp35-core-spec.md`; realizes **phase 2** of the roadmap.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

`Mcp35.Server` is the ergonomic SDK for **building** MCP servers over stdio. It
turns "register some tools + `Run()`" into a working server, so each concrete
server (`SerperMcpServer`, `FilesMcpServer`, …) stays tiny. It builds on
`Mcp35.Core` and adds no new managed dependencies.

---

## 0. Scope & constraints

- **In scope (phase 2):** the stdio server runtime, a tool registration API, the
  `initialize`/`tools/list`/`tools/call`/`ping` dispatch, and a `ProcessRunner`
  helper for git/cmd-style servers.
- **Out of scope:** resources, prompts, sampling, HTTP serving, dynamic tool
  sets (servers expose a **static** tool list — `listChanged = false`).
- Same constraints as Core: **net35**, no `async`/`Task`/`dynamic`, Newtonsoft
  only, **no `GxPT.*`**. curl-exec stays in Core (D9); this SDK adds only
  **process-exec**.

---

## 1. Namespace & file layout

Assembly `Mcp35.Server`, root namespace `Mcp35.Server`. `ProjectRef → Mcp35.Core`.

```
src/Mcp35.Server/
├─ McpServer.cs          runtime: registration + Run() dispatch loop
├─ ToolRegistration.cs   ToolHandler delegate, registered-tool record
├─ ToolResults.cs        result helpers (Text / Error / Json)
├─ SchemaBuilder.cs      minimal JSON-Schema helper (optional convenience)
├─ Process/
│  ├─ ProcessRunner.cs   external-process exec (git, cmd, …)
│  └─ ProcessRequest.cs / ProcessResult.cs
└─ StdErrLogSink.cs      ILogSink → Console.Error (never stdout!)
```

---

## 2. The `McpServer` runtime + registration API

```csharp
public sealed class McpServer
{
    public McpServer(Implementation serverInfo, ILogSink log);

    // Register a tool. inputSchema is a JSON Schema object (build via SchemaBuilder
    // or pass a JObject / JSON string). Throws on duplicate name.
    public void AddTool(string name, string description, JObject inputSchema,
                        ToolHandler handler);

    // Register cleanup to run during graceful shutdown (EOF or shutdown signal).
    public void OnShutdown(Action cleanup);

    // Blocks, running the stdin→stdout dispatch loop until graceful shutdown.
    public void Run();
    public void Run(Stream stdin, Stream stdout);   // overload for tests
}

public delegate CallToolResult ToolHandler(ToolCallContext ctx);

public sealed class ToolCallContext
{
    public string  ToolName { get; }
    public JObject Arguments { get; }   // raw MCP arguments (may be null → {})
    public ILogSink Log { get; }
    // No CancellationToken on net35; serial processing (§4) makes it moot.
}
```

A concrete server is then just:

```csharp
static void Main()
{
    var s = new McpServer(new Implementation { name = "serper", version = "1.0" },
                          new StdErrLogSink());
    s.AddTool("web_search", "Search the web.",
              SchemaBuilder.Object().Str("query", required: true).Build(),
              ctx => ToolResults.Text(DoSearch(ctx.Arguments.Value<string>("query"))));
    s.Run();   // blocks until EOF
}
```

---

## 3. Tool handler model + result helpers

- Handlers receive parsed `Arguments` (a `JObject`) and return a
  `CallToolResult` (the Core DTO).
- Result helpers keep handlers terse:
  ```csharp
  public static class ToolResults {
      static CallToolResult Text(string text);              // single text block
      static CallToolResult Json(object structured);        // structuredContent + text
      static CallToolResult Error(string message);          // isError = true
  }
  ```
- **Tool failures are values, not exceptions.** A handler signals failure by
  returning `ToolResults.Error(...)` (→ `isError = true`), which the host relays
  to the model. If a handler *throws*, the runtime catches it and converts it to
  an `isError` result (a tool bug must never crash the server) — see §4.

---

## 4. Dispatch loop & lifecycle

`Run()` is a **single-threaded, serial** loop (one request fully handled before
the next is read). Serial keeps the SDK trivial and is sufficient for a
single-client child process; a worker pool can come later if a server needs
concurrency.

```
loop:
  line = StdioFraming.ReadMessage(stdin)   // Core
  if line == null: break                   // stdin EOF → graceful shutdown
  msg = McpJson.Parse(line)
  if it's a notification (no id):
      "notifications/initialized" → mark ready
      "notifications/cancelled"   → ignore (serial: request already handled)
      "notifications/shutdown"    → break loop (GxPT-private; see Shutdown below)
      other                       → ignore
  else (request, has id):
      dispatch by method → write JsonRpcResponse via StdioFraming.WriteMessage
on exit: run OnShutdown hooks → flush → return (process exits 0)
```

Method handling:

| Method | Behavior |
|--------|----------|
| `initialize` | Reply `InitializeResult`: negotiate `protocolVersion` (echo client's if ≥ our floor, else our `Default`), `capabilities = { tools: { listChanged: false } }`, `serverInfo`. |
| `notifications/initialized` | Mark ready (no reply). |
| `tools/list` | Return all registered tools; `nextCursor = null` (no paging — small static sets). |
| `tools/call` | Resolve by `params.name`; unknown → `-32602` (invalid params). Invoke handler in `try/catch`; exception → `isError` result. Return `CallToolResult`. |
| `ping` | Reply `{}`. |
| anything else | `-32601` method not found. |

Error mapping discipline:
- **Framework/protocol problems** (bad JSON, unknown method, unknown tool name,
  malformed params) → **JSON-RPC error responses** (`-32700`/`-32601`/`-32602`).
- **Tool execution problems** → **`isError` results**, never JSON-RPC errors.
- A handler exception is always contained → `isError`, and logged to stderr.

### Graceful shutdown
MCP's stdio lifecycle defines **no standard `shutdown` RPC** — the canonical
graceful path is the client closing stdin (EOF), after which the server cleans
up and exits (the host SIGKILLs only after a grace period). So:
- **`stdin` EOF is the authoritative trigger.** On EOF the loop ends, **`OnShutdown`
  hooks run** (resource cleanup for Git/Command servers), stdout/stderr flush,
  and the process exits 0.
- Because GxPT controls both ends, the runtime *also* accepts an optional
  **GxPT-private `notifications/shutdown`** as an in-band "begin shutdown now"
  courtesy that runs the same sequence. It is **non-standard** — third-party
  servers built on the SDK simply never receive it and rely on EOF, and
  third-party servers GxPT connects to are shut down via EOF only.

---

## 5. stdout / stderr discipline (critical)

**`stdout` carries the JSON-RPC stream and nothing else.** A stray `Console.Write`
to stdout corrupts framing and breaks the connection. Therefore:
- All diagnostics go to **stderr** via `StdErrLogSink` (the host drains it to
  `GxPT.Logger`, as `CreateCompletionStream` already drains curl stderr).
- The runtime writes each response with `StdioFraming.WriteMessage` (UTF-8 +
  `\n` + flush) — one message per line, no embedded newlines.
- Servers must not let dependencies print to stdout; if a sub-process does, it's
  run through `ProcessRunner` (§6) which captures its stdout separately.

---

## 6. `ProcessRunner` (process-exec helper)

For `GitMcpServer` / `CommandMcpServer` and any server shelling out. Pure BCL,
mirrors the `ProcessStartInfo` pattern in `OpenRouterClient`.

```csharp
public sealed class ProcessRunner
{
    public ProcessResult Run(ProcessRequest req);
}

public sealed class ProcessRequest {
    public string FileName;                       // resolved exe path (injected/PATH)
    public string Arguments;                      // pre-escaped
    public string WorkingDirectory;
    public IDictionary<string,string> Environment;
    public string StdinText;                      // optional
    public int    TimeoutMs;                      // kill on overrun
}
public sealed class ProcessResult {
    public int    ExitCode;
    public string StdOut;                         // UTF-8 decoded
    public string StdErr;
    public bool   TimedOut;
}
```

- `UseShellExecute = false`, `CreateNoWindow = true`, redirected stdio, explicit
  `new UTF8Encoding(false, false)`.
- **Timeout** → kill the process tree and set `TimedOut`.
- Reads stdout/stderr on separate threads (or async streams) to avoid pipe
  deadlock on large output.
- Argument **escaping/quoting is the caller's responsibility** — `ProcessRunner`
  does not build command lines (the security-sensitive servers own that, §
  architecture §9).

---

## 7. Capabilities, paging, cancellation (phase-2 stance)

- **Capabilities:** advertise `tools` only, `listChanged = false` (static sets) —
  so no `notifications/tools/list_changed` is ever emitted.
- **Paging:** `tools/list` returns everything, `nextCursor = null`.
- **Cancellation:** `notifications/cancelled` is **ignored** — serial processing
  means the cancelled request has already completed (or hasn't started) by the
  time we can read the notification.

---

## 8. Testing — phase-2 exit criteria

`Mcp35.Server.Tests` (net48 linked-source, pinned Newtonsoft) drives `Run(stdin,
stdout)` with scripted in-memory streams and asserts:

1. **Handshake** — `initialize` → well-formed `InitializeResult` with negotiated
   version + `tools.listChanged = false`; `notifications/initialized` produces no
   reply.
2. **Listing** — `tools/list` returns exactly the registered tools with their
   `inputSchema` intact.
3. **Calling** — `tools/call` routes to the handler, passes `Arguments`, returns
   its `CallToolResult`.
4. **Errors** — unknown tool → `-32602`; unknown method → `-32601`; a handler
   that throws → `isError` result (process stays alive).
5. **Framing/stdout hygiene** — exactly one JSON message per line on stdout; logs
   land on stderr, not stdout.
6. **`ProcessRunner`** — exit code / stdout / stderr capture, timeout kill, and
   large-output (no-deadlock) cases.

Passing this unblocks phase 3 (the `Mcp35.Client` `StdioTransport` consuming a
real server) and phase 7 (the four concrete servers).

---

## 9. Resolved decisions

- **`SchemaBuilder` scope** — *resolved*: ship a **minimal fluent builder** for
  common object schemas, and **always allow raw** `JObject`/JSON strings for
  anything complex (§2–3).
- **Serial vs. concurrent dispatch** — *resolved*: **serial** (one request at a
  time, §4). Revisit only if a future server has long-running tools that must
  not block others (none of the four do today). `notifications/cancelled` stays
  a no-op while serial.
- **Graceful shutdown** — *resolved*: **stdin EOF** is the authoritative,
  standard trigger and runs **`OnShutdown` cleanup hooks** + clean exit; the
  runtime *additionally* honors a **GxPT-private `notifications/shutdown`**
  courtesy signal (non-standard; third parties rely on EOF) (§4 → Graceful
  shutdown).
