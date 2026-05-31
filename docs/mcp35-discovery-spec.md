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
  message, the **revealed set with an LRU cap**, and `reveal_tools` handling +
  call resolution.
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
   │                 names manifest, revealed set (LRU), reveal_tools
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
actively-used tool is never the LRU victim.

---

## 8. LRU cap & eviction

```csharp
void EnforceCap():
   while _revealed.Count > _revealCap:
      victim = key of _revealed with min tick
      _revealed.Remove(victim)
```

- The cap bounds **only the revealed set** (the full schemas in `tools`); the
  names manifest is always complete (cheap).
- `reveal_tools` is *not* in `_revealed` (it's prepended separately), so it can
  never be evicted.
- A single `Reveal` batch larger than the cap leaves only the most-recent `cap`
  callable; the model saw every requested schema in the result and can re-reveal.
- `_revealCap` default is a tuning value (§13).

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
4. **LRU** — exceeding the cap evicts least-recently-used; `TryResolve` bumps
   recency (prevents eviction of an active tool); evicted tool re-revealable.
5. **Lifecycle** — refresh prunes removed tools from catalog + revealed; remove
   drops a faulted server's tools so they can't be resolved.

Passing this completes the discovery layer; the roadmap then **splits `Mcp35.*`
into its own repo** before phases 6–8.

---

## 13. Open questions

- **`_revealCap` default** — generous enough that batches rarely exceed it
  (≈16–32?); tune empirically (this is the architecture §15 "reveal cap value").
- **Manifest format** — flat list vs grouped-by-server headers (both names-only);
  pick whichever the model navigates better in testing.
- **Manifest cadence** — *resolved*: cache the string, rebuild lazily on the next
  request after a catalog mutation (closes architecture §15 "manifest refresh
  cadence").
