# Mcp35.Core — Implementation Spec (Phase 1)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`;
realizes **phase 1** of its roadmap.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

`Mcp35.Core` is the role-neutral foundation: JSON-RPC 2.0 envelopes, MCP DTOs,
the transport seam, message framing, the SSE parser, and the shared curl-exec
helper. It contains **no transport implementations** (those are `Mcp35.Client`,
phases 3/8) and **no `GxPT.*` references**.

---

## 0. Constraints (recap)

- **net35**, old-format csproj. **No `async`/`await`, no `Task`, no `dynamic`,
  no TPL.** Concurrency = threads + `ManualResetEvent`/`Monitor` + callbacks,
  exactly as `OpenRouterClient.CreateCompletionStream` already does.
- **Only managed dependency: `Newtonsoft.Json`** (net35 build, vendored under
  `Mcp35.Core/lib/`). No `System.Web.Extensions`.
- **No `GxPT.*`** — anything host-specific (logging) is injected (seam rule #1).

---

## 1. Namespace & file layout

Assembly `Mcp35.Core`, root namespace `Mcp35.Core`.

```
src/Mcp35.Core/
├─ Json/
│  ├─ McpJson.cs            shared JsonSerializerSettings + Serialize/Parse
│  └─ RequestIdConverter.cs string|number|null id converter
├─ Rpc/
│  ├─ JsonRpcRequest.cs / JsonRpcNotification.cs / JsonRpcResponse.cs
│  ├─ JsonRpcError.cs / JsonRpcErrorCodes.cs
│  ├─ RequestId.cs
│  ├─ IRpcTransport.cs      the seam Mcp35.Client implements
│  ├─ JsonRpcPeer.cs        request/response correlation engine (full-duplex)
│  └─ IDuplexMessageChannel.cs
├─ Protocol/
│  ├─ McpMethods.cs         method-name constants
│  ├─ ProtocolVersions.cs   supported version strings
│  ├─ Initialize.cs         InitializeParams/Result, Implementation, capabilities
│  ├─ Tools.cs              Tool, ListTools*, CallTool*
│  └─ ContentBlock.cs       text/image/resource content
├─ Transport/
│  ├─ StdioFraming.cs       newline-delimited JSON read/write
│  ├─ SseParser.cs          text/event-stream → SseEvent
│  └─ CurlRunner.cs         curl process wrapper (buffered + streaming)
├─ Diagnostics/
│  └─ ILogSink.cs           injected logging seam
└─ Errors/
   └─ McpException.cs / McpTimeoutException.cs / McpTransportException.cs
```

---

## 2. JSON conventions (`Mcp35.Core.Json`)

A single shared configuration so every layer serializes identically.

```csharp
public static class McpJson
{
    public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
    {
        NullValueHandling     = NullValueHandling.Ignore,   // omit absent fields
        MissingMemberHandling = MissingMemberHandling.Ignore,
        DateParseHandling     = DateParseHandling.None,      // keep dates as strings
        FloatParseHandling    = FloatParseHandling.Double,
        MaxDepth              = 128,                         // explicit (D3 note)
        Formatting            = Formatting.None,
        Converters            = { new RequestIdConverter() },
    };

    public static string   Serialize(object value);
    public static T        Deserialize<T>(string json);
    public static JToken   Parse(string json);              // open-ended boundary
}
```

- **`DateParseHandling.None`** is deliberate: MCP carries dates as ISO strings
  inside open-ended payloads; we must *not* let Newtonsoft coerce them to
  `DateTime`.
- **Typed-DTO vs `JToken` boundary** (per architecture §3): stable envelopes are
  typed; open-ended fields — `Tool.inputSchema`, `CallToolParams.arguments`,
  content `data`, `result` bodies — are `JObject`/`JToken` and pass through
  losslessly.

### `RequestId` (`Mcp35.Core.Rpc`)
JSON-RPC `id` is `string | number | null`. Model it as a small value type with a
converter, so correlation keys are well-typed and round-trip exactly:

```csharp
public struct RequestId : IEquatable<RequestId>
{
    // holds either a long or a string; tracks which.
    public static RequestId FromLong(long n);
    public static RequestId FromString(string s);
    public bool IsNull { get; }
}
```
`RequestIdConverter` reads a JSON number → long, string → string, null → null;
writes back in kind. Equality/hashing back the pending-request table (§5).

---

## 3. JSON-RPC envelopes (`Mcp35.Core.Rpc`)

Request and Notification are **separate types** so field-presence is structural,
not a nullable flag — a notification is precisely "a request with no `id`":

```csharp
public sealed class JsonRpcRequest {
    [JsonProperty("jsonrpc")] public string JsonRpc = "2.0";
    [JsonProperty("id")]      public RequestId Id;        // always written
    [JsonProperty("method")]  public string Method;
    [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)]
    public JToken Params;                                  // omitted when null
}

public sealed class JsonRpcNotification {
    [JsonProperty("jsonrpc")] public string JsonRpc = "2.0";
    [JsonProperty("method")]  public string Method;       // NO id field at all
    [JsonProperty("params", NullValueHandling = NullValueHandling.Ignore)]
    public JToken Params;
}

public sealed class JsonRpcError {
    [JsonProperty("code")]    public int Code;
    [JsonProperty("message")] public string Message;
    [JsonProperty("data", NullValueHandling = NullValueHandling.Ignore)]
    public JToken Data;
}
```

**Responses need presence-aware parsing.** A valid `result` may itself be JSON
`null`, which `NullValueHandling.Ignore` would erase — so we never rely on a C#
null to decide result-vs-error. Parse the raw object and branch on **key
presence**:

```csharp
public sealed class JsonRpcResponse {
    public RequestId Id;
    public bool      IsError;
    public JToken    Result;   // present (possibly JToken null) when !IsError
    public JsonRpcError Error; // present when IsError

    public static JsonRpcResponse Parse(JObject o);
    //  → IsError = o["error"] != null; else Result = o["result"] (even if null)
}
```

### Standard error codes (`JsonRpcErrorCodes`)
`-32700` parse · `-32600` invalid request · `-32601` method not found · `-32602`
invalid params · `-32603` internal. Server/MCP-defined codes live outside the
`-32768…-32000` reserved band. **Tool failures are not protocol errors** — they
ride in `CallToolResult.isError` (§9), never as a `JsonRpcError`.

---

## 4. Transport seam (`Mcp35.Core.Rpc.IRpcTransport`)

The contract `Mcp35.Client`'s `StdioTransport`/`HttpTransport` implement and
`McpServerConnection` consumes. **Synchronous and blocking** — callers invoke it
from a worker thread, just like `CreateCompletionStream` is called off the UI
thread.

```csharp
public interface IRpcTransport : IDisposable
{
    void Start();                                        // spawn process / prepare
    bool IsConnected { get; }

    // Blocking request/response. Transport allocates the id and correlates.
    JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs);

    // Fire-and-forget client → server.
    void SendNotification(string method, JToken @params);

    // Server → client notifications (and, later, requests). Raised on the
    // transport's reader thread; the host marshals to the UI as needed.
    event EventHandler<JsonRpcInboundEventArgs> Inbound;
}
```

- Callers pass `method` + `params`; **id generation is internal** so nothing
  upstream manages ids.
- **Inbound server→client requests** (sampling/roots/elicitation) are surfaced
  via `Inbound` but **not auto-handled in phase 1**; the default policy (reply
  `-32601 method not found`) is a Client/host concern (open item §13).

### `JsonRpcPeer` — the correlation engine
The reusable half of a **full-duplex** transport (i.e. stdio). HTTP is
one-POST-one-response and does **not** need it.

```csharp
public interface IDuplexMessageChannel : IDisposable {
    void Start();
    void Send(string json);                 // one framed message
    event Action<string> MessageReceived;   // one framed message, reader thread
    event Action<Exception> Faulted;
}

public sealed class JsonRpcPeer : IDisposable   // implements the IRpcTransport core
{
    public JsonRpcPeer(IDuplexMessageChannel channel, ILogSink log);
    // owns: id counter, pending table, inbound dispatch (response vs notification)
}
```

`StdioTransport` (Client) = `JsonRpcPeer` over a stdio channel.
`HttpTransport` (Client) implements `IRpcTransport` directly atop `CurlRunner` +
`SseParser`.

---

## 5. Threading model (the load-bearing detail on net35)

- **`SendRequest` blocks** the calling (worker) thread until the matching
  response arrives or `timeoutMs` elapses.
- **Pending-request table:** `Dictionary<RequestId, PendingCall>` guarded by a
  `lock`. Each `PendingCall` holds a `ManualResetEvent` and a response slot.
  ```
  SendRequest:  id = next();  register(id);  channel.Send(json);
                if (mre.WaitOne(timeoutMs)) return slot; 
                else { unregister(id); throw McpTimeoutException; }
  inbound msg:  has id & (result|error)?  → complete that PendingCall, Set() its mre
                has method, no id?         → raise Inbound (notification)
                has method & id == ping    → auto-reply {} (keep-alive)
                has method & id (other)    → reply -32601, also raise Inbound
                top-level JSON array        → process each element (legacy batch)
  ```
  We **never emit** batches; the array case is inbound-tolerance only.
- **Full-duplex (stdio)** runs a **dedicated reader thread** pumping
  `MessageReceived`; responses Set pending events, notifications raise `Inbound`
  — all on the reader thread.
- **Disposal / fault:** close the channel, then fault every outstanding
  `PendingCall` (Set with an error) so no caller hangs; subsequent `SendRequest`
  throws `McpTransportException`.
- **Id generation:** `Interlocked.Increment` on a `long`, wrapped as
  `RequestId.FromLong`.

---

## 6. `CurlRunner` (`Mcp35.Core.Transport`)

A reusable curl wrapper distilled from `OpenRouterClient` (D9 — used by both
`HttpTransport` and `SerperMcpServer`). HTTP plumbing, but pure BCL + injected
paths, so it stays in Core without a native build dependency.

```csharp
public sealed class CurlRunner
{
    public CurlRunner(string curlPath, string caBundlePath, ILogSink log);

    CurlResult Run(CurlRequest req);                       // buffered body
    void RunStreaming(CurlRequest req, Action<string> onLine,
                      Action onDone, Action<string> onError);  // SSE lines
}

public sealed class CurlRequest {
    public string Url; public string Method = "POST";
    public string BodyJson;                                // → temp file, --data-binary @f
    public IDictionary<string,string> Headers;             // → -K config temp file
    public bool Stream;                                    // adds -N
    public int  TimeoutMs;
}
public sealed class CurlResult { public int HttpStatus; public string Body; public string Stderr; }
```

Mirrors the proven approach exactly:
- request body → **temp file**, passed via `--data-binary @file`;
- headers (incl. `Authorization`) → a **`-K` config temp file** so secrets/PAT
  never appear on the command line (architecture §8/§10);
- `--fail-with-body`, `-N` when streaming, `CreateNoWindow`, redirected stdio;
- **explicit `new UTF8Encoding(false, false)`** decode via `StreamReader` over
  `BaseStream`;
- **`try/finally` temp cleanup** (port `CleanupTempFiles` + `EscapeForCurlConfig`
  — Core gets its own copy; no `GxPT.*` reuse).

---

## 7. SSE parser (`Mcp35.Core.Transport.SseParser`)

`text/event-stream` → events; line-oriented to match curl's `ReadLine` loop.

```csharp
public sealed class SseEvent { public string EventType; public string Data; public string LastEventId; }

public sealed class SseParser {
    IEnumerable<SseEvent> PushLine(string line);  // emits on blank-line boundary
    SseEvent Flush();                             // trailing event at stream end
}
```

- Accumulates `data:` lines (multiple joined with `\n`), tracks `event:` / `id:`,
  ignores comment lines (`:`-prefixed) and handles CRLF/LF.
- For MCP Streamable HTTP each emitted `Data` is a complete JSON-RPC message →
  `McpJson.Parse` → `JsonRpcResponse.Parse`.
- A POST response may be `application/json` (single message) **or**
  `text/event-stream` (one or more messages); `HttpTransport` branches on the
  returned `Content-Type` (buffered `Run` vs `RunStreaming`).

---

## 8. Stdio framing (`Mcp35.Core.Transport.StdioFraming`)

MCP stdio is **newline-delimited JSON** (UTF-8; messages contain no embedded
newlines) — *not* LSP `Content-Length` framing.

```csharp
public static class StdioFraming {
    static void   WriteMessage(Stream stdin, string json);   // UTF-8 bytes + '\n' + flush
    static string ReadMessage(StreamReader stdout);          // one line, or null at EOF
}
```

- A reader thread loops `ReadMessage` → `MessageReceived` (the
  `IDuplexMessageChannel` for stdio).
- **stderr is logging, not protocol** — drained to `ILogSink`, as
  `CreateCompletionStream` drains curl stderr.

---

## 9. MCP protocol DTOs (`Mcp35.Core.Protocol`)

### Versions & methods
```csharp
public static class ProtocolVersions {
    public const string V2024_11_05 = "2024-11-05";   // old dual-endpoint HTTP+SSE (HTTP unsupported)
    public const string V2025_03_26 = "2025-03-26";   // Streamable HTTP — HTTP floor
    public const string V2025_06_18 = "2025-06-18";   // structured output; MCP-Protocol-Version header
    public const string V2025_11_25 = "2025-11-25";   // latest stable at time of writing

    public const string Default    = V2025_11_25;     // advertised in initialize (single knob)
    public const string HttpFloor  = V2025_03_26;     // min acceptable for HTTP servers
}
```

**Version policy — negotiation-tolerant, single advertised version + transport
floor** (not N codepaths):
- Advertise `Default` (latest validated stable) in `initialize`; **accept
  whatever version the server returns**.
- For **HTTP**, reject/disable a server below `HttpFloor` (`2025-03-26`) — older
  HTTP used a dual-endpoint transport we don't implement (non-goal). **Stdio**
  accepts down to `2024-11-05` (framing is identical across all revisions).
- Differences between revisions are **additive JSON**; tolerant parsing
  (`MissingMemberHandling.Ignore` + `JToken` passthrough) absorbs them, so there
  is **no per-version parsing code** — only the HTTP floor + the
  `MCP-Protocol-Version` header (post-init, HTTP) are version-aware.
- Functionally GxPT needs nothing beyond `2025-03-26` (tools over
  stdio/Streamable HTTP); advertising newer is for currency/optional features,
  and downgrades are always accepted.
- Conservative fallback if a strict server mishandles an unknown-newer version:
  set `Default = V2025_06_18`.

```csharp
public static class McpMethods {
    public const string Initialize        = "initialize";
    public const string Initialized       = "notifications/initialized";
    public const string ToolsList         = "tools/list";
    public const string ToolsCall         = "tools/call";
    public const string ToolsListChanged  = "notifications/tools/list_changed";
    public const string Ping              = "ping";
}
```
HTTP (GitHub) requires ≥ `2025-03-26` (Streamable HTTP). Advertise `Default`,
**negotiate down** to the server's returned `protocolVersion`, and echo the
negotiated value in the `MCP-Protocol-Version` header on subsequent HTTP
requests.

### Handshake DTOs
```csharp
public sealed class Implementation { public string name; public string version; }

public sealed class InitializeParams {
    public string protocolVersion;
    public JObject capabilities;          // ClientCapabilities (typed-ish, JToken escape)
    public Implementation clientInfo;
}
public sealed class InitializeResult {
    public string protocolVersion;
    public JObject capabilities;          // ServerCapabilities; we read capabilities.tools
    public Implementation serverInfo;
    public string instructions;           // optional
}
```
Capabilities are kept as `JObject` in phase 1 (we only need to detect
`capabilities.tools`); promote to typed sub-DTOs later if needed.

Phase 1 **advertises empty client capabilities** (no `roots`/`sampling`/
`elicitation`), so a compliant server issues no server→client requests beyond
`ping` (auto-answered, §5). Core ships these DTOs plus the `McpMethods` /
`ProtocolVersions` constants; **`Mcp35.Client` drives the handshake** (send
`initialize` → await `InitializeResult` → send `notifications/initialized`).

### Tools
```csharp
public sealed class Tool {
    public string  name;
    public string  description;           // may be long/absent — NOT used in the manifest
    public JObject inputSchema;           // JSON Schema, passed through verbatim
    public JObject annotations;           // optional
}
public sealed class ListToolsParams  { public string cursor; }
public sealed class ListToolsResult  { public List<Tool> tools; public string nextCursor; }

public sealed class CallToolParams   { public string name; public JObject arguments; }
public sealed class CallToolResult {
    public List<ContentBlock> content;
    public bool    isError;               // tool-level failure (NOT a JsonRpcError)
    public JObject structuredContent;     // optional
}
```
`nextCursor` ⇒ the registry pages `tools/list` until exhausted.

### Content blocks
```csharp
public sealed class ContentBlock {
    public string  type;                  // "text" | "image" | "audio" | "resource" | "resource_link"
    public string  text;                  // when type == "text"
    public JObject raw;                   // full block preserved for non-text types
    public bool   TryGetText(out string s);
}
```
Phase 1 fully models **text**; other types are preserved as `raw` with typed
accessors added as needed.

---

## 10. Errors & logging seams

```csharp
public class McpException          : Exception { public JsonRpcError Error; }   // server returned error
public class McpTimeoutException   : McpException { }
public class McpTransportException : McpException { }                            // channel/process failure

public interface ILogSink { void Log(string category, string message); }
```
- A JSON-RPC **error response** → `McpException(Error)`. A **tool** failure →
  returned normally as `CallToolResult{ isError = true }` (the model sees it).
- `ILogSink` is the only way Core logs; the host adapts `GxPT.Logger` to it
  (seam rule #1). A `NullLogSink` is the default.

---

## 11. Phase-1 exit criteria — the JSON spike

Build `Mcp35.Core.Tests` (net48 linked-source, version-pinned Newtonsoft) and
prove, against a **real GitHub MCP `tools/list`** capture and a **large
(>2 MB) tool result**:

1. **Field presence** — a `JsonRpcNotification` serializes with **no `id`**; a
   `JsonRpcRequest` always includes `id`; `params` is **omitted** when null.
2. **Result-vs-error** — `JsonRpcResponse.Parse` branches on key presence; a
   genuine `result: null` is treated as a result, not an error.
3. **Large payload** — deserialize the big result with **no length cap** and a
   set `MaxDepth`; no exceptions (the failure mode we picked Newtonsoft to
   avoid).
4. **Lossless open-ended fields** — `Tool.inputSchema` round-trips
   `JObject`-equal; `arguments`/content `data` pass through unchanged.
5. **Unicode + deep nesting** — survive non-ASCII and nesting near `MaxDepth`.

Passing this gate unblocks phase 2 (`Mcp35.Server`).

---

## 12. Dependency direction (sanity)

```
Mcp35.Core.Protocol ─┐
Mcp35.Core.Transport ─┼─► Mcp35.Core.Rpc ─► Mcp35.Core.Json ─► Newtonsoft.Json
Mcp35.Core.Errors ───┘                         (+ BCL only)
```
Nothing in Core references `Mcp35.Client`, `Mcp35.Server`, or `GxPT.*`. `Json`
is the innermost layer; `Rpc` builds on it; `Protocol`/`Transport`/`Errors`
build on `Rpc`.

---

## 13. Open questions (Core)

- **Protocol version** — *resolved*: negotiation-tolerant, advertise the latest
  validated stable (`ProtocolVersions.Default`, currently `2025-11-25`) with an
  HTTP floor of `2025-03-26`; tolerant parsing absorbs additive revision diffs.
  `Default` is the single knob; bump it when a newer revision is validated.
- **Server→client requests** — *resolved*: `JsonRpcPeer` auto-answers `ping`
  with `{}`, replies `-32601` to any other server→client request, and raises
  `Inbound` for observability; empty client capabilities mean none beyond `ping`
  are expected.
- **`RequestId`** — *resolved*: value-type struct (§2) for typed correlation
  keys.
- **SSE robustness** — line-oriented (matches curl `ReadLine`) is enough for
  phase 1; revisit byte-chunk reassembly only if a server splits lines oddly.
