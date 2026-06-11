# Tool-Call Loop — Implementation Spec (Phase 4)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`
(esp. §7) and the Core/Server/Client specs; realizes **phase 4**.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

Phase 4 is the **host-side** integration that makes tools actually usable: it
teaches GxPT's chat path to expose tools to the model, parse streamed
`tool_calls`, route them through `McpServerConnection`, and feed results back
until the model produces a final answer. It is **GxPT code** (`Services/Mcp/` +
small extensions to `OpenRouterClient`/`ChatModels`), not part of `Mcp35.*`.

---

## 0. Scope & constraints

- **In scope:** OpenRouter function-calling wire format, streaming `tool_calls`
  reassembly, the orchestration loop, the names-manifest + `reveal_tools`
  handling, and the MCP↔OpenRouter mapping.
- **Out of scope / stubbed:** real approval tiers (phase 6 — phase 4 uses an
  allow/confirm stub behind a clean hook); `HttpTransport`/GitHub (phase 8);
  rich transcript UI (medium item — phase 4 ships a minimal representation).
- Constraints: **net35**, no `async`/`Task`. **The host adopts Newtonsoft.Json**
  for the OpenRouter/MCP path (D16) — the wire code below uses `JObject`/typed
  DTOs, not JavaScriptSerializer — streaming via curl + worker
  threads as today.

---

## 1. What changes, and where

| Area | Change |
|------|--------|
| `ChatModels.cs` | `ChatMessage` gains optional `ToolCalls` + `ToolCallId`; `ChatCompletionChunk` gains `Delta.tool_calls` + `Choice.finish_reason` |
| `OpenRouterClient.cs` | **migrated to Newtonsoft** (D16): `BuildRequestBody` (now `JObject`-based) emits `tools`, assistant `tool_calls`, and `tool`-role messages; chunk parsing + error extraction use Newtonsoft; a lower-level `StreamRawChunks` surfaces parsed chunks |
| `ConversationStore.cs` | **migrated to Newtonsoft** (D16) to round-trip the new tool-call/`tool` messages; backward-compat golden-file tests on existing conversations |
| `Services/Mcp/ToolCallAssembler.cs` | reassembles fragmented streamed `tool_calls` |
| `Services/Mcp/McpChatOrchestrator.cs` | the loop: call model → run tools → re-call until final |
| `Services/Mcp/McpToolRegistry.cs` | name munging/bijection, manifest, revealed set + `reveal_tools` |

> JSON-library note (D16): the host uses **Newtonsoft** for the OpenRouter/MCP
> path and `ConversationStore`; `AppSettings` stays on JavaScriptSerializer for
> now. This unifies tool-argument handling (`JObject` end-to-end) and removes
> the `MaxJsonLength` cap from request serialization.

---

## 2. OpenRouter function-calling wire format

**Request** adds a `tools` array (and lets `tool_choice` default to `auto`):
```json
"tools": [
  { "type": "function",
    "function": { "name": "github__create_pull_request",
                  "description": "...",
                  "parameters": { /* JSON Schema = MCP inputSchema */ } } }
]
```

**Assistant tool-call response** (what the model returns):
```json
{ "role": "assistant", "content": null,
  "tool_calls": [ { "id": "call_abc", "type": "function",
                    "function": { "name": "github__create_pull_request",
                                  "arguments": "{\"title\":\"...\"}" } } ] }
```
`finish_reason == "tool_calls"`. `arguments` is a **JSON string**, not an object.

**Tool result** is fed back as a `tool`-role message keyed by the call id:
```json
{ "role": "tool", "tool_call_id": "call_abc", "content": "<result text>" }
```

**Streaming:** `tool_calls` arrive **fragmented** across SSE chunks — see §5.

---

## 3. MCP ↔ OpenRouter mapping

### Tool definition → function
`Tool { name, description, inputSchema }` →
`{ type:"function", function:{ name: <qualified>, description, parameters: inputSchema } }`.
The MCP `inputSchema` (a JSON Schema object) is the `parameters` verbatim.

### Name munging + bijection (critical)
OpenAI/OpenRouter require function names to match `^[a-zA-Z0-9_-]{1,64}$` — but
tool names must also be **server-qualified** for collision-safety (D11). So the
registry maintains a **bijection** `functionName ↔ (connection, originalToolName)`:
- qualified form `"<server>__<tool>"` (double-underscore separator; `:`/`/`
  are invalid in function names);
- **sanitize** server/tool to the allowed charset; if the result exceeds 64
  chars or collides, **truncate + append a short hash** and store the mapping.
- On a `tool_call`, the registry resolves `functionName` back to its connection
  and original name before calling.

### `reveal_tools` — host meta-tool (not from any server)
Always exposed: `{ name:"reveal_tools", description, parameters:{ names: string[] } }`.
When the model calls it, the **host handles it locally** (no MCP round-trip):
validate names against the manifest, add them to the **revealed set** (owned by
the Conversation; provider-gated eviction — see discovery spec §8 and
`prompt-caching-design.md`), and return the revealed tools' **full
definitions** as the tool result so the model can read their schemas. They
become callable on the next iteration.

### Result → `tool` message content
`CallToolResult.content[]` → a string for the `tool` message:
- text blocks concatenated;
- non-text blocks (image/resource) → a short textual placeholder noting type/uri
  (rich handling deferred);
- `isError == true` → the error text is still returned as content (the model
  sees and can recover — **not** an exception);
- `structuredContent`, if present → appended as compact JSON.

---

## 4. Model / message extensions

```csharp
// ChatModels.cs
internal sealed class ToolCall {            // assistant → tool calls
    public string Id;
    public string Name;                     // qualified function name
    public string ArgumentsJson;            // raw JSON string from the model
}

internal sealed class ChatMessage {
    public string Role;                     // + "tool"
    public string Content;
    public List<AttachedFile> Attachments;
    public List<ToolCall> ToolCalls;        // assistant messages (null otherwise)
    public string ToolCallId;               // "tool" messages (null otherwise)
}

internal sealed class ChatCompletionChunk {
    // Delta gains tool_calls; Choice gains finish_reason
    internal sealed class Delta {
        public string role; public string content;
        public List<ToolCallDelta> tool_calls;
    }
    internal sealed class Choice { public Delta delta; public string finish_reason; }
}
internal sealed class ToolCallDelta {       // streamed fragment
    public int index; public string id; public string type;
    public FunctionDelta function;
}
internal sealed class FunctionDelta { public string name; public string arguments; }
```

`BuildRequestBody` becomes `List<Dictionary<string,object>>` (not `…,string>`) so
it can emit `tool_calls` on assistant messages and `tool_call_id` on tool
messages, and adds the `tools` array when provided. The emoji system message is
preserved; the **names manifest** (§7) is added as an extra system message.

---

## 5. Streaming `tool_calls` reassembly (`ToolCallAssembler`)

The fiddly core: providers stream tool calls in pieces — `id`/`name` usually in
the first fragment, `arguments` dribbled across many. Fragments are correlated
by **`index`**.

```
state: Dictionary<int, Acc>   // Acc { Id, Type, Name, ArgsSb }

onChunk(chunk):
  choice = chunk.choices[0]
  if choice.delta.content   → forward to UI (onTextDelta)
  for f in choice.delta.tool_calls ?? []:
      a = state[f.index] ??= new Acc
      if f.id        != null: a.Id    = f.id
      if f.type      != null: a.Type  = f.type
      if f.function?.name      != null: a.Name += f.function.name        // concat-safe
      if f.function?.arguments != null: a.ArgsSb.Append(f.function.arguments)
  if choice.finish_reason != null: finalize(choice.finish_reason)

finalize(reason):
  if state non-empty (reason == "tool_calls" or stream end with calls):
     calls = state ordered by index → ToolCall{ Id, Name, ArgsJson = ArgsSb (or "{}") }
     emit onToolCalls(calls)
  else: emit onAssistantText(accumulatedContent)   // plain answer
```

- Handles both fragmented and whole-in-one-chunk providers.
- Empty `arguments` → `{}`. `ArgsJson` is parsed to `JObject` only when about to
  call (so a malformed args string surfaces as a tool error, not a crash).
- `finish_reason == "length"` mid-call → truncated tool call → surface as error.

`OpenRouterClient` exposes the chunk stream the assembler consumes:
```csharp
void StreamRawChunks(string body, Action<ChatCompletionChunk> onChunk, Action<string> onError);
```
(`CreateCompletionStream` becomes a thin text-only wrapper over it; same curl
plumbing, SSE loop, UTF-8 decode, temp cleanup as today.)

---

## 6. The orchestration loop (`McpChatOrchestrator`)

```
RunTurn(userText, ui):
  history.Add(user, userText)
  for i in 1..MaxIterations:                     // default 8
     tools    = registry.ExposedFunctionDefs()   // reveal_tools + revealed set
     manifest = registry.NamesManifestSystemMessage()
     body     = BuildRequestBody(model, manifest+history, tools, props{stream:true})

     assembler = new ToolCallAssembler(onTextDelta: ui.AppendDelta)
     client.StreamRawChunks(body, assembler.OnChunk, ui.OnError)

     if assembler.ProducedToolCalls:
        history.Add(assistant, content: assembler.Text, toolCalls: calls)
        foreach c in calls:                       // serial (phase 4)
           result = ExecuteCall(c)                // §reveal_tools / approval / connection
           history.Add(tool, toolCallId: c.Id, content: result)
        continue                                  // re-call the model
     else:
        history.Add(assistant, assembler.Text); ui.Complete(); return

  // loop cap hit
  history.Add(assistant, "[Tool-call limit reached.]"); ui.Complete()

ExecuteCall(c):
  if c.Name == "reveal_tools":
     names = parse(c.ArgsJson).names
     return registry.Reveal(names)               // returns revealed defs as text
  (conn, toolName) = registry.Resolve(c.Name)     // bijection; unknown → error text
  decision = approval.Check(c.Name, args)         // phase-4 stub: Allow
  if decision == Deny: return "[Call denied by user.]"
  try    { return Format(conn.CallTool(toolName, parse(c.ArgsJson), CallTimeoutMs)) }
  catch  (McpException ex)          { return "[Tool error: " + ex.Message + "]" }
  catch  (McpTransportException ex) { mark conn faulted; return "[Server unavailable.]" }
```

- **Bounded** by `MaxIterations` to prevent loops.
- **Multiple tool calls** in one response are executed **serially** in phase 4
  (matches the serial server stance; parallel is a later optimization).
- **Tool failures are fed back as content**, so the model can recover rather
  than the turn aborting. Only an unrecoverable client/UI error stops the turn.

---

## 7. Names manifest + `reveal_tools`

- Each request, the registry builds a **system message** listing every available
  **qualified function name** (names only — no schemas, per the discovery
  decision), e.g. a compact list. This is cheap and keeps discovery
  deterministic.
- The exposed `tools` array each turn = **`reveal_tools`** + the **revealed
  set** (full schemas, name-sorted for prompt-cache stability; conversation-
  owned with provider-gated eviction — see discovery spec §8). The model reads
  the manifest → calls `reveal_tools(names)` → those defs join the `tools`
  array next iteration → it calls them.
- `reveal_tools` execution is local (host): validate names, update the
  conversation's revealed list (bump recency), return the revealed schemas as
  the tool result.

---

## 8. Approval hook (stub in phase 4)

```csharp
public interface IToolApprovalPolicy { ApprovalDecision Check(string functionName, JObject args); }
public enum ApprovalDecision { Allow, Deny }
```
Phase 4 wires an **allow-all** (or simple per-call confirm) implementation; phase
6 replaces it with the tiered allowlist/remember + always-confirm-destructive
model. The loop already calls it at the right point (`ExecuteCall`).

---

## 9. Persistence & transcript (minimal in phase 4)

- **Persistence (resolved):** assistant `tool_calls` and `tool`-role results are
  part of the conversation context, so `ConversationStore` **persists** them
  (round-trips the extended schema) — without this, reloading a conversation
  drops tool context. This is the trigger for migrating `ConversationStore` to
  Newtonsoft (§1, D16), done with backward-compat tests on existing files.
- **Transcript UI:** phase 4 renders a **minimal** marker per tool call/result
  in `MainForm` (e.g. a "called `name`(…) → result" line, plain text). Rich,
  collapsible rendering is the separate transcript-UI item.

---

## 10. Threading / UI marshaling

- The loop runs on a **worker thread** (as chat sends already do). `StreamRawChunks`
  blocks it; text deltas marshal to the UI via the existing `Invoke` path.
- Tool execution (`conn.CallTool`) blocks the same worker thread — fine; the UI
  shows a "running tool…" indicator. The whole turn is one worker-thread job.

---

## 11. Testing — phase-4 exit criteria

1. **Reassembly** — `ToolCallAssembler` rebuilds one and multiple tool calls from
   fragmented chunk sequences; whole-in-one-chunk; empty args → `{}`; truncated
   (`finish_reason:length`) → error.
2. **Mapping** — name munging bijection (sanitize, 64-char/collision via hash);
   `CallToolResult` (text / isError / structured) → correct `tool` content.
3. **Loop** — single tool call → result → final answer; multiple calls in a
   turn; `reveal_tools` reveals then a follow-up call succeeds; `MaxIterations`
   cap; tool error fed back (model can continue).
4. **Wire format** — `BuildRequestBody` emits valid `tools`, assistant
   `tool_calls`, and `tool` messages (golden-JSON assertions).
5. **End-to-end** — with a stub `IRpcTransport`/registry, a scripted model
   stream drives a full call→result→answer turn.

Passing this means tools work end to end (over stdio); phase 6 hardens approval,
phase 8 adds GitHub over HTTP.

---

## 12. Resolved decisions

- ~~Conversation persistence of tool messages~~ — *resolved*: **persist** (extend
  `ConversationStore` schema; migrate it to Newtonsoft, D16) for correct context
  on reload.
- ~~Host JSON library~~ — *resolved*: **Newtonsoft** for the OpenRouter/MCP path
  + `ConversationStore`; `AppSettings` deferred (D16).
- ~~Serial vs parallel tool execution within a turn~~ — *resolved*: **serial** in
  phase 4 (one call fully handled before the next; matches the server SDK's serial
  dispatch); parallel only if latency later warrants.
- ~~`MaxIterations` default~~ — *resolved*: **8**, a named constant (tunable). A
  turn that hits the cap returns with a note rather than looping unbounded.
- ~~Non-text content blocks~~ — *resolved*: a **minimal textual placeholder** in
  phase 4 (e.g. `[image]` / `[resource: <uri>]`); richer inline rendering later.
- ~~`tool_choice`~~ — *resolved*: leave at **`auto`**; no forced-tool path in
  phase 4.
