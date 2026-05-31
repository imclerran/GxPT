# HttpTransport + GitHub MCP — Implementation Spec (Phase 8)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`,
`mcp35-core-spec.md` (`CurlRunner` / `SseParser` / `IRpcTransport`), and
`mcp35-client-spec.md` (`McpServerConnection`, where `HttpTransport` is the
phase-3 stub). Realizes **phase 8** — the final piece.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

Phase 8 implements `Mcp35.Client.HttpTransport` (Streamable HTTP over curl) and
wires up the **GitHub MCP** server through its `mcp.json` entry + PAT shape gate.
The phase-5 names-manifest / `reveal_tools` machinery is what makes GitHub's
large tool count tractable, so by the time we get here the hard part (catalog
scale) is already solved — phase 8 is "add a second transport behind the same
seam."

---

## 0. Scope & constraints

- **In scope:** `HttpTransport : IRpcTransport` (the POST request/response cycle,
  `application/json` vs `text/event-stream` branching, the MCP session +
  required headers, init handshake, graceful `DELETE` teardown); the GitHub
  integration (config entry, host-side PAT shape gate, annotation-driven
  classification).
- **Out of scope:** the **standalone GET listening stream** for unsolicited
  server→client messages (sampling/roots/elicitation) — GitHub's tool surface
  doesn't need it; we handle only inbound messages that arrive *on a POST's own
  SSE response*. Also out: OAuth flows (GitHub uses a static PAT bearer), HTTP
  resumability/`Last-Event-ID` replay.
- Same library constraints: **net35**, no `async`/`Task`/`dynamic`, Newtonsoft
  only, **no `GxPT.*`**. `HttpTransport` lives in `Mcp35.Client`; all curl/SSE
  mechanics are reused from **`Mcp35.Core`** (D9), curl + CA-bundle paths
  **injected** (same as `OpenRouterClient`).

---

## 1. `HttpTransport` — `IRpcTransport` directly over curl

Unlike `StdioTransport`, HTTP is **one POST = one correlated response**, so it
needs **no `JsonRpcPeer`** (no shared pending table, no id-correlation engine —
each `SendRequest` *is* its own curl invocation and gets *its own* response). It
implements the seam directly atop Core's `CurlRunner` + `SseParser`.

```csharp
public sealed class HttpTransport : IRpcTransport       // Mcp35.Core.Rpc.IRpcTransport
{
    public HttpTransport(string url, IDictionary<string,string> headers,
                         CurlRunner curl, ILogSink log);

    public void Start();                                 // no-op: HTTP is connectionless until first POST
    public bool IsConnected { get; }                     // true once a session is established
    public JsonRpcResponse SendRequest(string method, JToken @params, int timeoutMs);
    public void SendNotification(string method, JToken @params);   // POST, expect 202, no body
    public event EventHandler<JsonRpcInboundEventArgs> Inbound;    // notifications seen on a POST's SSE
    public void Dispose();                               // DELETE session, then release
}
```

- **`Start()` is a no-op.** There's no persistent socket to open; "connected" is a
  *protocol* state (a session exists), established by the first `initialize` POST.
  `McpServerConnection.Open` drives the handshake transport-agnostically — it
  calls `SendRequest("initialize", …)` exactly as it does over stdio.
- **Id generation stays internal** (seam contract): `HttpTransport` allocates a
  monotonic id per `SendRequest`, stamps it on the outbound message, and verifies
  the returned message's id matches (a mismatch → fault for that call).
- **Concurrency is free:** each `SendRequest` spawns its own curl process and
  blocks the *calling* worker thread on that process — multiple in-flight
  requests are naturally independent, no locking around a pending table.

## 2. The request/response cycle

Every JSON-RPC request is one `CurlRequest` (`POST`, `BodyJson` = the framed
message, headers per §3). The reply's `Content-Type` decides how the response is
read:

```
SendRequest(method, params, timeoutMs):
  id   = NextId()
  body = McpJson.Serialize(JsonRpcRequest{ id, method, params })
  res  = curl.Run(CurlRequest{ Url, "POST", body, Headers(), Stream=true, timeoutMs })
  // -D <hdrfile> captured headers; branch on Content-Type:
  if status is 2xx and body is application/json:
        msg = single JSON-RPC message
  else if text/event-stream:
        parse with SseParser; for each emitted Data → JSON-RPC message:
            - notification / server→client request  → raise Inbound
            - response whose id == id               → this is our answer
  else: map error (§6)
  return the matched JsonRpcResponse           // its result/error flows to the caller
```

- **`Run` vs `RunStreaming`:** since a single POST may legitimately come back as
  SSE (the server's choice), `HttpTransport` always requests with `Accept` for
  both and treats the body uniformly — it feeds the captured body through
  `SseParser` when `Content-Type: text/event-stream`, else parses it as one
  message. (For GitHub's request/response tools a single `application/json` body
  is the common case; SSE handling is there for spec-completeness and servers
  that stream tool progress.)
- **Notifications** (`SendNotification`) POST the message and expect **`202
  Accepted`** with no body; any 2xx is accepted, non-2xx is logged (a dropped
  `notifications/initialized` shouldn't fault the session).

## 3. Session, headers & lifecycle

MCP Streamable HTTP layers a **session** and **version** negotiation on top of
HTTP. The handshake mirrors `OpenRouterClient`'s curl discipline (temp-file body,
`-K` config for secret headers, header dump via `-D`):

**Headers on every request:**
| Header | Value |
|--------|-------|
| `Accept` | `application/json, text/event-stream` (required) |
| `Content-Type` | `application/json` (on POST) |
| `Authorization` | from the `mcp.json` entry's `headers` (e.g. `Bearer <PAT>`) — via `-K`, never the command line |
| `MCP-Protocol-Version` | the **negotiated** version — sent on **every request after** `initialize` |
| `Mcp-Session-Id` | the captured session id — sent on every request after `initialize`, **if** the server issued one |

**Handshake (driven by `McpServerConnection.Open`):**
1. POST `initialize` (no session/version header yet). Capture **`Mcp-Session-Id`**
   from the response headers (`-D <tempfile>`). Parse `InitializeResult`; negotiate
   the protocol version.
2. **Floor check:** the negotiated version must be **≥ `HttpFloor` (2025-03-26)**
   (`ProtocolVersions`, Core §9). Below floor → fault → host disables the server
   (older dual-endpoint HTTP+SSE is unsupported).
3. POST `notifications/initialized` (now carrying `MCP-Protocol-Version` +
   `Mcp-Session-Id`). → `IsConnected = true`.
4. Normal operation: `tools/list`, `tools/call`, … all carry the two headers.

**Teardown (`Dispose`):** if a session id was issued, send an HTTP **`DELETE`** to
the same URL with the session header to terminate the session server-side, then
release temp files. A `DELETE` failure is logged, not thrown (best-effort, same
as curl temp cleanup).

## 4. Inbound & duplex scope

- Inbound **notifications** that appear on a POST's SSE response are surfaced via
  the `Inbound` event (e.g. progress, `tools/list_changed`) — `McpServerConnection`
  already listens and re-fires `ToolsChanged`, so a GitHub catalog change
  invalidates the host's manifest (architecture §15 close-out).
- Inbound **server→client requests** (sampling/roots/elicitation): per the seam's
  phase-1 stance, the default reply is **`-32601` method not found**; we don't
  implement a standalone GET listening stream to receive *unsolicited* ones. This
  matches GitHub MCP, which is request/response over POST.

## 5. GitHub MCP integration

GitHub is **not** first-party — it's an ordinary Tier-2 `mcp.json` HTTP server
(architecture §8), distinguished only by a seeded entry and a PAT shape gate.

- **Config:** the seeded entry
  ```json
  "github": { "url": "https://api.githubcopilot.com/mcp/",
              "headers": { "Authorization": "Bearer YOUR_GITHUB_PAT" } }
  ```
  `url` present → the host infers HTTP and builds an `HttpTransport(url, headers,
  curl, log)`.
- **PAT shape gate (host-side, `McpHost`/config layer — not the transport):**
  before constructing the connection, validate the bearer matches a well-formed
  GitHub PAT (`ghp_…` classic or `github_pat_…` fine-grained). If it doesn't —
  including the unedited `YOUR_GITHUB_PAT` placeholder — the host **silently
  disables** the entry: no connection attempt, no error spam (architecture §8,
  D15). The transport itself never sees a malformed token.
- **Scale is already handled:** GitHub exposes *many* tools. The host's phase-5
  **names manifest + `reveal_tools`** (with the LRU reveal cap) is exactly what
  keeps that from flooding the model context — qualified names like
  `github__create_pull_request` / `github__list_issues` are listed, full defs
  revealed on demand.
- **Classification:** GitHub tools are third-party → classified from **MCP
  annotations** (advisory, `mcp35-approval-spec.md` §2): `readOnlyHint` →
  ReadOnly (search/list/get), `destructiveHint` → Destructive, otherwise → Write
  (create/update/merge). The PAT only ever travels in the `-K` header file.

## 6. Errors & resilience

- **curl non-zero / timeout** → fault that `SendRequest` (mapped to a JSON-RPC
  error result the caller surfaces); never throws across the seam.
- **HTTP status mapping:** `4xx/5xx` with a JSON-RPC error body → return that
  error; bare `401/403` → auth failure → fault the connection (host may surface
  "check your PAT"); `404` on a *post-init* request → likely an **expired
  session** → fault (phase 8 does **not** auto-reinitialize — keep it simple;
  re-open is a host decision).
- **Malformed SSE / unparseable body** → fault that call, log the raw (PAT
  redacted) to stderr/`ILogSink`.
- **No retries in the transport** — request-level retry/backoff, if ever wanted,
  is host policy (the network-retry guidance is for git ops, not tool calls).

---

## 7. Testing — phase-8 exit criteria

`Mcp35.Client.Tests` (net48 linked-source) with curl **stubbed** via an injected
fake `CurlRunner` (returns canned status/headers/body), so no network:
1. **Handshake** — `initialize` POST → `InitializeResult`; `Mcp-Session-Id`
   captured from headers and replayed on subsequent requests; `MCP-Protocol-
   Version` present post-init, absent on the first `initialize`.
2. **Floor gate** — negotiated version `< 2025-03-26` → connection faults
   (server disabled).
3. **Content-Type branch** — an `application/json` reply and a `text/event-stream`
   reply to the *same* request both yield the correct `JsonRpcResponse`;
   intervening SSE notifications raise `Inbound`.
4. **Correlation** — response id mismatch → that call faults; concurrent
   `SendRequest`s (separate curl invocations) don't cross responses.
5. **Notifications** — `notifications/initialized` POSTs and accepts `202`; a
   non-2xx notification is logged, not fatal.
6. **Teardown** — `Dispose` issues a `DELETE` with the session header; failure is
   swallowed; temp files cleaned.
7. **GitHub gate (host test)** — placeholder/malformed PAT → entry disabled, no
   transport built; well-formed `ghp_…`/`github_pat_…` → connection attempted;
   PAT never appears on a curl command line (only in the `-K` file).

Passing this completes the MCP feature end-to-end: stdio first-party servers +
HTTP GitHub, behind one registry, one approval gate, one tool-call loop.

---

## 8. Decisions to confirm

1. **Session expiry on 404** — *recommended*: **fault, don't auto-reinitialize**
   (simplest; the host can re-open). (Alt: transparently re-handshake and replay
   — more robust but adds reconnect state to a deliberately stateless transport.)
2. **Standalone GET listening stream** — *recommended*: **out of scope** (no
   unsolicited server→client messages; inbound only on POST SSE). (Alt: add the
   GET stream for full duplex — only needed if we later support
   sampling/roots/elicitation.)
3. **Per-call retry** — *recommended*: **none in the transport** (tool calls are
   user-visible and gated; silent retries could double a side effect). (Alt:
   bounded retry on idempotent reads only.)
4. **CA bundle** — *recommended*: inject the same CA-bundle path the OpenRouter
   curl path already uses (one source of truth). (Alt: a dedicated MCP bundle.)
