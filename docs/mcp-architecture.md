# MCP Support — Architecture & Plan

**Status:** Design (planning). No implementation yet.
**Branch:** `claude/mcp-server-architecture-cJ088`
**Last updated:** 2026-05-29

This document captures the agreed architecture for adding Model Context Protocol
(MCP) support to GxPT: a reusable .NET 3.5 MCP library (**Mcp35**) split into
**Core / Client / Server**, plus an MCP **host** embedded in GxPT and a set of
MCP **servers**. It is the reference for implementation; it is deliberately
decision-oriented rather than code.

---

## 1. Goals & non-goals

### Goals
- Let GxPT act as an MCP **host**, calling tools exposed by MCP servers during a
  chat (via OpenRouter function-calling).
- Ship a small, reusable, role-neutral MCP library (**Mcp35**) targeting **.NET
  Framework 3.5**, usable outside GxPT later — for *both* writing clients/hosts
  and writing servers.
- Provide a handful of useful **stdio servers** (serper, file ops, git,
  command-line) and connect to **GitHub's hosted MCP** over HTTP.
- Keep token cost bounded as the tool catalog grows (progressive disclosure /
  progressive disclosure via a name manifest + define-on-demand).

### Non-goals (initially)
- OAuth for HTTP MCP (GitHub uses a PAT via curl `-K`).
- A standalone server-initiated GET event stream (HTTP transport is
  POST-per-request, SSE parsed from the POST response).
- Building the net35 app under a modern SDK (unchanged constraint — see §11).

---

## 2. Decision ledger (quick reference)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Library name is **Mcp35**; projects `Mcp35.Core`, `Mcp35.Client`, `Mcp35.Server` (the server-building SDK, **singular**) | Neutral, descriptive (nods to net35); chosen now to avoid a later rename |
| D2 | **Mono-repo first**, split Mcp35 into its own repo later | API churns hard through early phases; ProjectReference beats DLL-republish during churn |
| D3 | JSON via **Newtonsoft.Json** (vendored, net35 build) | Declarative JSON-RPC field-presence control; no `MaxJsonLength` cap; GxPT has no Newtonsoft to conflict with |
| D4 | **Host-side discovery: a names-only manifest up front, full schemas revealed on demand by exact name** (`reveal_tools`, batch). No free-text search tool for now (deferred until/if the catalog grows huge) | Deterministic discovery (no hallucinated tools, no empty-result retries) at minimal token cost; multi-reveal handles ambiguity without summaries |
| D5 | HTTP target = **GitHub MCP only**, Streamable HTTP, **PAT via curl `-K`** | No OAuth scope; reuse existing curl plumbing |
| D6 | Approval = **allowlist/remember**, but **execution/destructive tools always confirm** | cmd / git-writes / GitHub-writes are never silently auto-run |
| D7 | **Dependencies are injected, never embedded** in the library | Keeps Core binary-pure and the future split cheap |
| D8 | **`Mcp35.Client` is a real, separate project now** (transports + **single-connection** lifecycle); GxPT depends on it **one-way** | A compile-time project boundary prevents client logic from intertwining with GxPT — stronger than "extract later" |
| D9 | **curl-exec helper lives in `Mcp35.Core`** | Shared by Client `HttpTransport` and the Serper server; avoids duplication |
| D10 | **Each server is its own independent `.csproj`** depending on mcp35; extractable to its own repo. Not part of the mcp35 library package | Maximum modularity — servers can each peel off as standalone projects/repos |
| D11 | **Multi-server aggregation, collision-naming, ranking & reveal all live in the GxPT host registry**; `Mcp35.Client` stays strictly single-connection | Keeps Client minimal; host owns all multi-server policy |
| D12 | **net48 test projects resolve Newtonsoft via version-pinned `PackageReference`** | Consistent with `GxPT.Tests`; pin matches the vendored net35 dll to avoid drift |
| D13 | **Concrete servers get neutral names** (`SerperMcpServer`, `FilesMcpServer`, `GitMcpServer`, `CommandMcpServer`) — *not* `Mcp35.Servers.*` | They're independent consumers (D10), not a collection inside the `Mcp35.Server` SDK |

---

## 3. Layered architecture

```
            GxPT host (net35 WinForms) — Services/Mcp/
            McpHost (owns N connections) · McpToolRegistry (aggregation +
            collision-naming + names-manifest/reveal_tools) · OpenRouter loop ·
            approval tiers · settings binding
                          │  ProjectReference  (one-way: GxPT → Client)
                          ▼
            Mcp35.Client (net35 lib) — single-connection
            StdioTransport · HttpTransport          (IRpcTransport impls)
            McpServerConnection (handshake / caps / tools-cache / correlation)
                          │ builds on
                          ▼
   Mcp35.Core (net35 lib)  ◄──────── builds on ────────  Mcp35.Server (net35 lib)
   JSON-RPC envelopes · MCP DTOs ·                       tool registration API ·
   IRpcTransport seam · stdio framing ·                  stdio dispatch loop ·
   SSE parser · shared curl-exec helper                  process-exec helper
   deps: Newtonsoft.Json only · NO GxPT refs                      ▲
                                                                  │ builds on
                                                       MCP server exes (independent consumers)
                                                       SerperMcpServer · FilesMcpServer ·
                                                       GitMcpServer · CommandMcpServer
```

Dependency arrows only ever point **down/inward**. GxPT references **only**
`Mcp35.Client` (which transitively pulls Core). GxPT does **not** reference
Server — servers are independent executables.

### Mcp35.Core (role-neutral, binary-pure)
- JSON-RPC 2.0 envelopes (request / response / notification / error).
- MCP DTOs: `initialize`/capabilities, `tools/list`, `tools/call`, content
  blocks, error objects.
- The **transport seam**: `IRpcTransport` (`SendRequest` + notifications +
  lifecycle). Core never knows whether it's talking over stdio or HTTP.
- Stdio newline framing + SSE event parser (operate on streams/strings handed
  in).
- **Shared curl-exec helper** (`CurlRunner` — pure BCL process spawn + temp
  files, curl path **injected**). Used by both Client's `HttpTransport` and the
  Serper server, so it lives here rather than being duplicated.
- JSON strategy: **typed DTOs** for stable envelopes; **`JObject`/`JToken`** at
  the open-ended boundaries (tool input-schemas, tool results).
- **Only** managed dependency is `Newtonsoft.Json`. No reference to `GxPT.*`.

### Mcp35.Client (the client-side protocol mechanics)
- `IRpcTransport` implementations:
  - `StdioTransport` — child process + std pipes.
  - `HttpTransport` — curl-per-POST via Core's curl-exec helper; `-K` auth,
    `-N`, UTF-8 decode, temp cleanup; adds `Accept` + `MCP-Protocol-Version`
    headers, captures `Mcp-Session-Id` (`-D <tempfile>`), `DELETE` on teardown.
- `McpServerConnection` — one server: `initialize` handshake, capability
  negotiation, `tools/list` caching, `tools/call` request/response correlation,
  lifecycle state machine, notifications.
- **Single-connection by design** — no multi-connection facade. Managing the
  *collection* of connections and aggregating their catalogs is host policy
  (D11); `Mcp35.Client` exposes one `McpServerConnection` per server and nothing
  more.
- Depends on Core + an injected curl path. **No OpenRouter, no approval UI, no
  settings types, no `GxPT.*`.**

### Mcp35.Server (builds on Core)
- Ergonomic tool registration: `name + description + input-schema + handler
  delegate`.
- The stdio dispatch loop (read framed requests on stdin, write responses on
  stdout).
- A **process-exec** helper (for git/cmd-style servers). curl-exec is *not*
  here — it lives in Core (D9), since `SerperMcpServer` and the client's
  `HttpTransport` both need it.
- The four servers are its first consumers and its dogfooding.

### GxPT host (`Services/Mcp/`) — application policy only
Everything here is GxPT-specific orchestration, deliberately kept *out* of the
library:
- `McpHost` — owns the **collection** of `Mcp35.Client.McpServerConnection`
  instances (one per configured server) and their lifecycle.
- `McpToolRegistry` — **catalog aggregation** across connections,
  **server-qualified collision naming**, **manifest assembly** (qualified names
  only), the **internal full-def cache**, and the **`reveal_tools`
  define-on-demand policy** incl. the LRU reveal cap (D11, §7). Resolves a tool
  name back to its owning connection.
- The **OpenRouter** tool-call loop (`tools`/`tool_calls` wiring) — OpenRouter-
  specific, not even MCP.
- Approval tiers + confirmation UI.
- Settings binding (`mcpServers`).

### MCP server executables (independent consumers, net35 console exes)
Each is its own project/repo (D10) consuming the `Mcp35.Server` SDK; neutrally
named because they are *not* part of the library (D13).
- `SerperMcpServer` — web search (Serper API; needs curl for TLS 1.2).
- `FilesMcpServer` — file read/list/write.
- `GitMcpServer` — git status/diff/commit (uses `git.exe`).
- `CommandMcpServer` — arbitrary command execution (sharpest security edge;
  always-confirm).

### Mechanics vs. policy — the boundary that must hold
| Concern | Home |
|---------|------|
| Transports (stdio / http), framing, SSE | `Mcp35.Client` (Core for parsers/curl-exec) |
| Connection lifecycle, handshake, caps, tools cache, call correlation | `Mcp35.Client` |
| Catalog aggregation + server-qualified collision naming | **GxPT host** (`McpToolRegistry`) |
| Names manifest + internal def cache + `reveal_tools` (LRU cap) | **GxPT host** |
| OpenRouter `tools`/`tool_calls` loop | **GxPT host** |
| Approval tiers + confirmation UI | **GxPT host** |
| `mcpServers` settings binding | **GxPT host** |

---

## 4. Repo strategy: mono-repo first → split later

We develop Mcp35 **inside this repo** while its API churns (phases 1–5), then
split `Mcp35.*` into its own `mcp35` repo once stable (~after phase 5). During
the mono phase GxPT uses a **`ProjectReference`** to `Mcp35.Client`; post-split
it swaps to the **vendored-DLL `HintPath`** pattern (identical to curl /
DotNetZip / itextsharp today).

> Making `Mcp35.Client` a real project *now* (D8) is itself the strongest form
> of seam rule #4: the compiler rejects an accidental `Client → GxPT` reference,
> so client logic physically cannot intertwine with the host. This is why we
> build it now rather than carving it out of GxPT later.

### The four seam rules (what keeps the split cheap)
1. **One-way dependency, enforced.** `Mcp35.Core` / `.Client` / `.Server` must
   never reference `GxPT.*` — not Logger, not settings, not UI. Anything
   host-specific is injected (delegate/interface), the same pattern as
   `curlPath`.
2. **Neutral namespace from commit one** — `Mcp35.*` (no later rename).
3. **Library binary-pure / deps injected** (see §6) — this *is* the seam
   enforcement, not just distribution hygiene.
4. **Separate projects + separate test projects.** Project boundaries make the
   compiler *reject* a reverse-reference. (Client included — D8.)

### Split mechanics (later)
- `git subtree split` peels the **library** (`Mcp35.Core` + `.Client` +
  `.Server`) into the new `mcp35` repo **with history preserved**. Each server
  is independent (D10) and can be subtree-split into its *own* repo separately,
  depending on the vendored mcp35 DLLs.
- GxPT swaps `ProjectReference` → vendored `Mcp35.Client.dll` /
  `Mcp35.Core.dll` in `GxPT/lib/` via `HintPath`.
- Because the dependency was already one-way, that swap is the *only* code
  change.
- Newtonsoft travels **with Core** (vendored under `Mcp35.Core/lib/`).

---

## 5. Project & solution layout

Mirrors GxPT's existing **dual-world** pattern:
- **VS2008-format `.sln`** (`GxPT.sln`, Format 10.00) holds the net35
  buildable projects.
- **SDK-style net48 test projects** link source files and run via `dotnet
  test` in CI; they are deliberately **kept out of the `.sln`** (as
  `GxPT.Tests` already is).

```
GxPT.sln                         (VS2008 format)
├─ GxPT/                         net35 WinExe (host code under Services/Mcp/)
├─ GxPT.Setup/                   installer (vdproj)
├─ src/Mcp35.Core/              net35 lib   (+ lib/Newtonsoft.Json.dll)
├─ src/Mcp35.Client/            net35 lib   (ProjectRef → Mcp35.Core)
├─ src/Mcp35.Server/            net35 lib   (ProjectRef → Mcp35.Core)  ← server SDK
└─ servers/                     independent consumer exes (own projects/repos)
   ├─ SerperMcpServer/          net35 Exe   (ProjectRef → Mcp35.Server)
   ├─ FilesMcpServer/           net35 Exe
   ├─ GitMcpServer/             net35 Exe
   └─ CommandMcpServer/         net35 Exe

(outside the .sln, run via dotnet test)
├─ GxPT.Tests/                  net48, linked-source  (existing)
├─ tests/Mcp35.Core.Tests/      net48, links Core sources, PackageRef Newtonsoft
├─ tests/Mcp35.Client.Tests/    net48, links Client sources + loopback transport
└─ tests/Mcp35.Server.Tests/    net48, links Server sources + fake transport
```

- New net35 projects use **old-format MSBuild** (`ToolsVersion 3.5`, xmlns
  `.../2003`, `<TargetFrameworkVersion>v3.5`), matching `GxPT.csproj`.
- GxPT references **`Mcp35.Client`** via `<ProjectReference>` during the mono
  phase (and `Mcp35.Core` transitively). GxPT does **not** reference Server.
- Each server is an **independent project** (own `.csproj`, `ProjectRef →
  Mcp35.Server`) that merely *consumes* the library, so each can later split
  into its own repo depending on vendored mcp35 DLLs (D10). They're neutrally
  named (D13), not under `Mcp35.`. The `servers/` folder is organizational, not
  a namespace.

---

## 6. Dependency policy

**Managed deps of the library: exactly one — `Newtonsoft.Json`** (net35 build,
vendored under `Mcp35.Core/lib/`). Core / Client / Server reference only
Newtonsoft + BCL. No `System.Web.Extensions`.

**Native tools are injected, never embedded.** curl is a *runtime external
process*, not a managed reference, and it does not flow transitively in the
HintPath world — so each deployable artifact vendors what **it** runs. The
**curl-exec helper code** lives in `Mcp35.Core` (D9), but the **curl binary** is
always supplied by whoever runs the process:

| Artifact | Native need | Source of the binary |
|----------|-------------|----------------------|
| GxPT host (Client `HttpTransport` → GitHub) | curl | **already vendored** in `GxPT/lib/`; host injects the path into the transport |
| `SerperMcpServer` | curl (TLS 1.2 to Serper API) | own `lib/curl.exe` next to the exe |
| `GitMcpServer` | `git.exe` | injected path → PATH |
| `FilesMcpServer` / `CommandMcpServer` | none | BCL only |
| `Mcp35.Core` / `.Client` / `.Server` (DLLs) | none | pure managed |

Path resolution everywhere: injected explicit path → app-relative
`./lib/<tool>` → PATH (same fallback the host already uses).

> Note on TLS: .NET 3.5's `HttpWebRequest` can't reliably negotiate TLS 1.2,
> which is why curl is vendored. Anything hitting a modern HTTPS endpoint
> (GitHub, Serper) needs curl — `WebClient` is not a substitute.

### Why Newtonsoft (and not JavaScriptSerializer / SimpleJson)
- **JSON-RPC field presence is semantic**: a notification is a request with
  `id` *omitted* (not null); `params` is omitted when empty; `result`/`error`
  are mutually exclusive. Newtonsoft expresses this declaratively
  (`NullValueHandling.Ignore`, `[JsonProperty]`); JavaScriptSerializer cannot
  without per-type custom converters.
- **No `MaxJsonLength`**: JavaScriptSerializer throws above ~2 MB unless reset
  at every call site — a latent crash exactly where large tool results live.
- **`JObject`/`JToken`** give null-safe navigation for the open-ended schema /
  result boundary.
- SimpleJson was considered (single-file, no DLL) but on net35 (no `dynamic`)
  its DOM degrades to dictionary-casting ≈ JavaScriptSerializer, it's dormant
  since ~2015, and its only unique wins don't apply here. Dropped.
- Config note: set Newtonsoft `MaxDepth` explicitly if MCP/JSON-Schema payloads
  nest beyond the default (64).

---

## 7. Tool discovery & invocation (host)

`BuildRequestBody` gains a `tools` array; the chunk DTO/parser gains
`tool_calls`.

**Discovery = name manifest + define-on-demand** (chosen over free-text search,
D4):

1. The host injects a lightweight **catalog manifest** into context each
   request — every tool's server-qualified **name only**, with no descriptions
   and no input schemas. The model thus always knows exactly what exists. (The
   host keeps the full defs from `tools/list` **cached internally**; it just
   doesn't push them to the model yet.)
2. The only real tool exposed initially is a meta-tool
   **`reveal_tools(names: string[])`** — an exact-name **lookup**, not a search.
   It returns the requested tools' **full definitions** and injects them into
   the callable `tools` array on the next turn. Being batch (plural), the model
   can resolve an ambiguous name by revealing **several** candidates at once and
   comparing their schemas.
3. The model emits a normal `tool_call`; `McpToolRegistry` resolves the owning
   `McpServerConnection` (D11) → approval check (§9) → route via that
   connection's `CallTool` → append a `tool`-role result → re-invoke (bounded
   iterations).

**Why names-first beats free-text search:** discovery is *deterministic* — the
model selects from ground-truth names, so it can't hallucinate a capability or
burn turns on empty-result searches — while token cost stays minimal (bare
qualified names are a tiny fraction of full schemas).

**Design notes:**
- **Names only — no summaries.** Server-supplied descriptions aren't guaranteed
  to be concise or well-authored, and summarizing them would mean a lossy
  truncation or an extra LLM call. Disambiguation is handled instead by
  **multi-reveal** (step 2).
- **Bounded reveal set.** A cap limits how many revealed **definitions** ride in
  the `tools` array (the names manifest is always fully present — it's cheap).
  When a reveal would exceed the cap, evict the **least-recently-used** def;
  recency is touched on both reveal *and* call, and `reveal_tools` itself is
  **never evicted**. Eviction is graceful: an evicted tool is simply absent from
  `tools` (so uncallable) but still in the manifest, so the model just
  re-reveals it — no error path. A single reveal batch may momentarily exceed
  the cap; LRU trims back on later turns.

> **Deferred:** no free-text search tool for now — `reveal_tools` over a
> names manifest is adequate for GxPT's initial catalog. If the catalog ever
> grows large enough that the names manifest itself becomes heavy, a
> `search_tools(query)` ranking over the host's internal cache can be added
> then.

---

## 8. Configuration (settings.json)

Extend `AppSettings` with an `mcpServers` collection; per entry:
- `name`
- `transport`: `stdio` | `http`
- stdio fields: `command`, `args`, `env` (e.g. Serper API key via `env`)
- http fields: `url`, `auth` (PAT stored like the existing API key, passed to
  curl via `-K`, never on the command line)
- enabled-tools + remembered-approval state (§9)

---

## 9. Approval & security model

- **Allowlist / remember** for read-only / low-risk tools.
- **Always confirm**, regardless of remembered state, for execution &
  destructive tools: command execution, git writes (commit/push), GitHub writes.
- `CommandMcpServer` is the sharpest edge: always-confirm + process isolation;
  treat all server/tool output as untrusted input at the parse boundary.

---

## 10. Transports (in `Mcp35.Client`)

- **Stdio**: child process; newline-framed JSON-RPC on stdin/stdout; stderr
  surfaced via an injected log delegate.
- **HTTP (GitHub)**: Streamable HTTP, curl-per-POST through Core's curl-exec
  helper. Reuses the OpenRouter-proven approach: temp-file request body, `-K`
  auth config, `-N`, UTF-8 decode, temp cleanup. Adds `Accept: application/json,
  text/event-stream`, `MCP-Protocol-Version`, captures `Mcp-Session-Id`
  (`-D <tempfile>`), `DELETE`s the session on teardown. SSE responses parsed by
  Core's SSE parser.

---

## 11. Build & CI

- The shipping **net35** assemblies need **VS2008 or msbuild + the v3.5
  targeting pack** to build — a stock modern SDK cannot (same constraint as the
  GxPT app today).
- But Core / Client / Server are **pure logic (no WinForms)**, so CI coverage
  comes via the **net48 linked-source test projects** run with `dotnet test` —
  the proven `GxPT.Tests` trick. Those projects link the library `.cs` files and
  reference Newtonsoft (PackageReference resolves a net4x build).
- Extend `.github/workflows/tests.yml` to also run `Mcp35.Core.Tests`,
  `Mcp35.Client.Tests`, and `Mcp35.Server.Tests`.
- A fake/loopback `IRpcTransport` backs Client and host tests.

---

## 12. Testing strategy

- `Mcp35.Core.Tests` (net48, xunit, linked-source): JSON-RPC
  serialization/round-trips, field-presence (notification vs request, omitted
  `params`), SSE parsing, stdio framing, large-payload handling.
- `Mcp35.Client.Tests`: `McpServerConnection` handshake/lifecycle and
  `tools/call` correlation against a loopback transport; transport framing.
- `Mcp35.Server.Tests`: registration + dispatch loop against a fake transport.
- Host tests in `GxPT.Tests`: catalog aggregation + collision naming, names
  manifest, `reveal_tools` define-on-demand + LRU eviction (incl. re-reveal of
  an evicted tool), and the OpenRouter tool-call loop driving fake
  `McpServerConnection`s.

---

## 13. Phasing / roadmap

1. **Mcp35.Core** + Newtonsoft wiring + a depth spike on a real GitHub MCP
   `tools/list` and a large tool result (validate field-presence + big
   payloads).
2. **Mcp35.Server** + one trivial server (validate the stdio loop).
3. **Mcp35.Client** `StdioTransport` + `McpServerConnection`; GxPT host consumes
   it and lists tools.
4. **Tool-call loop** + OpenRouter `tools`/`tool_calls` — first real
   end-to-end invocation.
5. **Tool discovery**: names manifest + `reveal_tools` (define-on-demand + LRU
   cap).
   → *Split `Mcp35.*` into its own repo here once the API is stable.*
6. **Approval tiers** (gate Command / git writes).
7. **Four servers**, riskiest last: Files → Serper → Git → Command.
8. **`Mcp35.Client` HttpTransport + GitHub MCP** (the names manifest +
   `reveal_tools` now tame its large tool count).

---

## 14. Risks

- OpenRouter function-calling fidelity across models.
- Newtonsoft `MaxDepth` on deeply nested schemas (config, de-risk in phase 1).
- curl process-per-call latency.
- `CommandMcpServer` security surface.
- Mono-repo coupling creep defeating the future split — mitigated by the §4
  seam rules and the now-enforced `Mcp35.Client` project boundary (D8).

---

## 15. Open / soft decisions

**Resolved (2026-05-29):**
- ~~Server home at split~~ → each server is its own independent project/repo
  consuming mcp35; the library repo holds only Core/Client/Server (D10).
- ~~Catalog aggregation boundary~~ → host registry owns aggregation + collision
  naming + ranking + reveal; Client stays single-connection (D11).
- ~~Newtonsoft net48 build in tests~~ → version-pinned PackageReference (D12).

- ~~Server project naming~~ → `Mcp35.Server` is the singular SDK; concrete
  servers get neutral names (`SerperMcpServer`, …), not `Mcp35.Servers.*` (D13).
- ~~Tool discovery mechanism~~ → names-only manifest with define-on-demand via
  batch `reveal_tools` (multi-reveal for ambiguity); no free-text search tool —
  deferred until/if the catalog grows huge (D4).

**Still open:**
- **Reveal cap value**: how many tool defs may ride in `tools` at once before
  LRU eviction kicks in — tune empirically.
- **Manifest refresh cadence**: rebuilt per request vs cached until a server's
  `tools/list` changes.

**Deferred (revisit only if the catalog grows huge):**
- A `search_tools(query)` fallback ranking over the host's internal cache, to
  narrow the names manifest before revealing.
