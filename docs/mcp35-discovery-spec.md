# Tool Discovery / `McpToolRegistry` — Implementation Spec (Phase 5)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`
(§7), `mcp35-toolloop-spec.md` (§3/§6/§7); realizes **phase 5**.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

Phase 5 implements the host's discovery component — **`McpToolRegistry`** — which
turns the aggregated catalog of every connected server's tools into (a) a cheap
**names manifest**, (b) a small set of **revealed** tool definitions, and (c)
`reveal_tools`, the meta-tool that moves a tool from "known by name" to
"callable." It is **GxPT host code** (`Services/Mcp/`), not part of `Mcp35.*`.

This is the last phase before the **repo split** (architecture §4).

---

## 0. Scope & constraints

- **In scope:** catalog aggregation across `McpServerConnection`s,
  server-qualified **name munging + bijection**, the names manifest system
  message, the **revealed set** (conversation-owned; see the prompt-caching
  revision note in §8), and `reveal_tools` handling + call resolution.
- **Out of scope:** the loop itself (phase 4), approval (phase 6), ranking /
  `search_tools` (deferred). All multi-server policy lives here (D11).
- Constraints: **net35**, Newtonsoft (`JObject`) per D16, thread-safe (touched
  from the orchestrator worker thread *and* connection reader threads).

---

## 1. Where it sits

```
McpHost  ──owns──►  McpServerConnection × N        (phase 3)
   │                      │ Ready / ToolsChanged / faulted
   ▼                      ▼
McpToolRegistry  ◄── aggregates tools/list, owns names↔(conn,tool) bijection,
   │                 names manifest, reveal_tools; reveal STATE lives on the
   │                 Conversation (see §8 revision note)
   ▼
McpChatOrchestrator (phase 4): NamesManifestSystemMessage() + ExposedFunctionDefs()
                               → BuildRequestBody;  Reveal() / TryResolve() on tool_calls
```

---

## 2. Data model

```csharp
sealed class CatalogEntry {
    public McpServerConnection Conn;
    public string  OriginalName;     // the server's tool name
    public string  FunctionName;     // munged, OpenAI-legal, server-qualified
    public string  Description;
    public JObject Schema;           // MCP inputSchema, passed through as `parameters`
}
```

Internal state (all under one `lock`):
- `Dictionary<string,CatalogEntry> _byFunctionName` — the catalog (full set of
  every connected server's tools).
- `Dictionary<McpServerConnection,List<string>> _byConnection` — for refresh/remove.
- `Dictionary<string,long> _revealed` — function name → recency tick (the
  exposed set; size-capped).
- `long _tick`, `int _revealCap`, a cached manifest string + dirty flag.

---

## 3. Name munging + bijection

OpenAI/OpenRouter function names must match `^[A-Za-z0-9_-]{1,64}$`, yet names
must also be **server-qualified** (collision-safe, D11). The mapping is
**deterministic and stable** across runs/refreshes (keyed on identity, not
insertion order), so the model never sees a tool's name change between turns.

```
Munge(serverName, toolName):
  raw  = serverName + "__" + toolName                 // double-underscore join
  base = each char of raw: [A-Za-z0-9_-] kept, else '_'
  if base.Length <= 64 AND base maps to this same (conn,tool):
      name = base
  else:                                                // too long, or collision
      h      = ShortHash(serverName + "\0" + toolName) // 6 hex, deterministic
      name   = Truncate(base, 64 - 7) + "_" + h
  bind name → CatalogEntry(conn, toolName); return name
```

- **Collisions** (two distinct `(server,tool)` sanitizing to the same `base`) are
  broken by the hash suffix.
- `reveal_tools` is reserved and never produced by `Munge`.

---

## 4. Names manifest (system message)

A compact, **names-only** system message rebuilt from the cached catalog
(invalidated whenever the catalog mutates — see §9; rebuild is cheap):

```
Available MCP tools. Call reveal_tools({"names":[...]}) to load a tool's full
definition before you can call it.
- files__read
- files__write
- git__status
- github__create_pull_request
- github__list_issues
...
```

Names only — **no descriptions, no schemas** (discovery decision, D4). Sorted by
server then name for stability/readability.

---

## 5. Exposed tool definitions (the request `tools` array)

```csharp
IList<JObject> ExposedFunctionDefs():
   result = [ RevealToolsDef ]                          // always present, pinned
   for fn in _revealed ordered by recency:
       e = _byFunctionName[fn]
       result.Add({ "type":"function",
                    "function": { "name": e.FunctionName,
                                  "description": e.Description,
                                  "parameters": e.Schema } })
   return result
```

`RevealToolsDef` is a constant:
```json
{ "type":"function",
  "function":{ "name":"reveal_tools",
    "description":"Load full definitions for tools by exact name so they become callable.",
    "parameters":{ "type":"object",
      "properties":{ "names":{ "type":"array", "items":{ "type":"string" } } },
      "required":["names"] } } }
```

---

## 6. `reveal_tools` handling

```csharp
string Reveal(string[] names):
   lock:
     revealedNow = []
     foreach n in names:
        if _byFunctionName.ContainsKey(n):
            _revealed[n] = ++_tick                       // add or bump recency
            revealedNow.Add(_byFunctionName[n])
        else:
            note "unknown tool: " + n
     EnforceCap()                                        // §8 (won't drop revealedNow)
   // result the model reads immediately: the requested defs' schemas + any notes
   return Serialize(revealedNow as {name, description, parameters}) + notes
```

- The **result returns all requested defs** (so the model sees schemas at once,
  enabling multi-reveal disambiguation), even if the cap later trims some from
  the *exposed* set.
- A tool revealed but later **evicted** (§8) is simply absent from `tools` and
  thus not callable — its name remains in the manifest, so the model re-reveals.
  Graceful, no error path (matches Core §… / architecture §7).

---

## 7. Resolution + recency (on `tool_calls`)

```csharp
bool IsRevealTools(string functionName) => functionName == RevealToolsName;

bool TryResolve(string functionName, out McpServerConnection conn, out string toolName):
   lock:
     if _byFunctionName.TryGetValue(functionName, out e):
        if _revealed.ContainsKey(functionName): _revealed[functionName] = ++_tick // keep alive
        conn = e.Conn; toolName = e.OriginalName; return true
     conn = null; toolName = null; return false
```

Resolving (i.e. the model actually calling a tool) **bumps recency**, so an
actively-used tool is never the eviction victim. *(Revised: recency now lives
on the conversation's reveal list and is bumped by the orchestrator, not in
`TryResolve` — see §8.)*

---

## 8. Reveal-set ownership & eviction

> **Revised by the prompt-caching work** (see `prompt-caching-design.md`,
> which is authoritative for everything in this section). The original
> registry-owned LRU described below was replaced because it broke provider
> prompt caching two ways: the recency-sorted emission reordered the `tools`
> array (position 0 of the prompt) whenever a tool was called, and the
> registry-global set let concurrent tabs churn each other's arrays.

Current model:

- **Reveal state lives on the `Conversation`** (`RevealedTools`, persisted,
  restored on reopen), threaded through `Reveal` / `ExposedFunctionDefs` by
  the orchestrator. The registry is stateless with respect to reveals.
- **Emission is name-sorted**, never reveal- or recency-ordered, so the
  serialized `tools` array is byte-stable across requests.
- The list itself is **recency-ordered** (reveal and call both bump a name to
  the end), used only for eviction order.
- **Eviction is provider-gated, at turn start only** (`McpChatOrchestrator`):
  prompt-caching providers never evict (an evicted def re-bills the whole
  cached transcript — always a net loss); non-caching providers trim to
  `RevealEvictionCap` (24), least recently used first.
- `reveal_tools` is prepended separately and can never be evicted; stale
  revealed names (server removed/disabled) are skipped at emission time, not
  pruned, so a returning server restores its defs.

---

## 9. Connection lifecycle integration

Driven by `McpHost` from `McpServerConnection` events (phase 3):

```csharp
void AddConnection(conn):       // conn reached Ready
   foreach t in conn.ListTools(false):
       fn = Munge(conn.Name, t.name)
       _byFunctionName[fn] = new CatalogEntry(conn, t.name, fn, t.description, t.inputSchema)
       _byConnection[conn].Add(fn)
   MarkManifestDirty()

void RefreshConnection(conn):   // conn raised ToolsChanged
   remove conn's old entries (from _byFunctionName, _byConnection, _revealed)
   AddConnection(conn) using conn.ListTools(true)

void RemoveConnection(conn):    // conn faulted / closed
   foreach fn in _byConnection[conn]:
       _byFunctionName.Remove(fn); _revealed.Remove(fn)
   _byConnection.Remove(conn); MarkManifestDirty()
```

- A tool that disappears on refresh is pruned from the catalog **and** the
  revealed set, so stale tools can't be called.
- All three run under the lock; `ToolsChanged`/fault fire on a connection reader
  thread, so locking is mandatory.

---

## 10. Threading

- Single `lock` guards all state. Methods are short (dictionary ops + string
  build); no I/O under the lock — `ListTools` may hit the transport, so call it
  **outside** the lock and merge results in (the lifecycle methods snapshot the
  tool list first, then lock to mutate).
- `ExposedFunctionDefs` / `NamesManifestSystemMessage` are called per request on
  the worker thread; `Add/Refresh/RemoveConnection` from reader threads — the
  lock serializes them.

---

## 11. API surface

```csharp
public sealed class McpToolRegistry
{
    public const string RevealToolsName = "reveal_tools";
    public McpToolRegistry(int revealCap, ILogSink log);

    // lifecycle (from McpHost)
    public void AddConnection(McpServerConnection conn);
    public void RefreshConnection(McpServerConnection conn);
    public void RemoveConnection(McpServerConnection conn);

    // per-request (from the orchestrator)
    public string         NamesManifestSystemMessage();
    public IList<JObject> ExposedFunctionDefs();

    // tool_call handling
    public bool   IsRevealTools(string functionName);
    public string Reveal(string[] names);
    public bool   TryResolve(string functionName, out McpServerConnection conn, out string toolName);
}
```

---

## 12. Testing — phase-5 exit criteria

`GxPT.Tests` (the registry needs no server — fake `McpServerConnection`s or a
small seam returning canned `Tool` lists):

1. **Munging/bijection** — qualification, charset sanitize, 64-char truncation +
   hash, collision disambiguation, determinism across re-adds, `reveal_tools`
   never collides.
2. **Manifest** — names-only, sorted, reflects adds/refreshes/removes; cached +
   invalidated on mutation.
3. **Reveal** — `ExposedFunctionDefs` always leads with `reveal_tools`; revealing
   adds defs; the result returns all requested schemas; unknown names noted.
4. **Reveal-set stability** *(revised; see §8)* — the registry never evicts
   from the caller's list; exposed defs are name-sorted regardless of reveal or
   call order; duplicate names emit one def. Provider-gated eviction is the
   orchestrator's job and covered by its tests.
5. **Lifecycle** — refresh prunes removed tools from the catalog and exposure
   (the conversation's list keeps the name; emission skips it); remove drops a
   faulted server's tools so they can't be resolved.

Passing this completes the discovery layer; the roadmap then **splits `Mcp35.*`
into its own repo** before phases 6–8.

---

## 13. Resolved questions

- **Reveal cap default** — *resolved*: **24** (mid of the 16–32 range), a named
  constant so it stays tunable without a design change (closes architecture §15
  "reveal cap value"). *(Revised: now `McpChatOrchestrator.RevealEvictionCap`,
  enforced only on non-caching providers — see §8.)*
- **Manifest format** — *resolved*: **flat list** of qualified names. The
  `server__tool` prefix already groups names visually (`github__…`, `files__…`),
  so explicit per-server headers add tokens for little gain; revisit only if
  testing shows the model navigates a grouped layout meaningfully better.
- **Manifest cadence** — *resolved*: cache the string, rebuild lazily on the next
  request after a catalog mutation (closes architecture §15 "manifest refresh
  cadence").
