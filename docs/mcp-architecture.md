# MCP Support — Architecture & Plan

**Status:** Design (planning). No implementation yet.
**Branch:** `claude/mcp-server-architecture-cJ088`
**Last updated:** 2026-05-29

This document captures the agreed architecture for adding Model Context Protocol
(MCP) support to GxPT: a reusable .NET 3.5 MCP library (**Mcp35**), an MCP
**host/client** embedded in GxPT, and a set of MCP **servers**. It is the
reference for implementation; it is deliberately decision-oriented rather than
code.

---

## 1. Goals & non-goals

### Goals
- Let GxPT act as an MCP **host/client**, calling tools exposed by MCP servers
  during a chat (via OpenRouter function-calling).
- Ship a small, reusable, role-neutral MCP library (**Mcp35**) that targets
  **.NET Framework 3.5**, usable outside GxPT later.
- Provide a handful of useful **stdio servers** (serper, file ops, git,
  command-line) and connect to **GitHub's hosted MCP** over HTTP.
- Keep token cost bounded as the tool catalog grows (progressive disclosure /
  tool-search).

### Non-goals (initially)
- OAuth for HTTP MCP (GitHub uses a PAT via curl `-K`).
- A standalone server-initiated GET event stream (HTTP transport is
  POST-per-request, SSE parsed from the POST response).
- Building the net35 app under a modern SDK (unchanged constraint — see §11).

---

## 2. Decision ledger (quick reference)

| # | Decision | Rationale |
|---|----------|-----------|
| D1 | Library name is **Mcp35**; projects `Mcp35.Core`, `Mcp35.Server`; servers `Mcp35.Servers.*` | Neutral, descriptive (nods to net35); chosen now to avoid a later rename |
| D2 | **Mono-repo first**, split Mcp35 into its own repo later | API churns hard through early phases; ProjectReference beats DLL-republish during churn |
| D3 | JSON via **Newtonsoft.Json** (vendored, net35 build) | Declarative JSON-RPC field-presence control; no `MaxJsonLength` cap; GxPT has no Newtonsoft to conflict with |
| D4 | **Tool-search is a host feature**, not a server | Only the host aggregates every server's tools; ranking lives where the catalog lives |
| D5 | HTTP target = **GitHub MCP only**, Streamable HTTP, **PAT via curl `-K`** | No OAuth scope; reuse existing curl plumbing |
| D6 | Approval = **allowlist/remember**, but **execution/destructive tools always confirm** | cmd / git-writes / GitHub-writes are never silently auto-run |
| D7 | **Dependencies are injected, never embedded** in the library | Keeps Core binary-pure and the future split cheap |

---

## 3. Layered architecture

```
┌──────────────────────────────────────────────────────────────┐
│ GxPT (host repo, net35 WinForms)                               │
│   Services/Mcp/  →  McpHost, McpServerConnection,              │
│                     StdioTransport, HttpTransport,             │
│                     McpToolRegistry (catalog + tool-search)    │
│   reuses existing curl plumbing for HTTP                       │
└───────────────▲───────────────────────────▲──────────────────┘
                │ ProjectReference (mono)     │ uses
                │ → vendored DLL (post-split) │
┌───────────────┴──────────────┐   ┌──────────┴───────────────────┐
│ Mcp35.Server (net35 lib)     │   │ Mcp35.Servers.* (net35 exes) │
│   tool registration API,     │   │   Serper, Files, Git, Command│
│   stdio dispatch loop,        │   │   (reference consumers of    │
│   process/curl exec helpers   │   │    Mcp35.Server)             │
└───────────────▲──────────────┘   └──────────────────────────────┘
                │ builds on
┌───────────────┴──────────────────────────────────────────────┐
│ Mcp35.Core (net35 lib)                                         │
│   JSON-RPC 2.0 envelopes, MCP DTOs, transport seam            │
│   (IRpcTransport), stdio framing, SSE parser                  │
│   deps: Newtonsoft.Json only. NO native, NO GxPT refs.        │
└──────────────────────────────────────────────────────────────┘
```

### Mcp35.Core (role-neutral, binary-pure)
- JSON-RPC 2.0 envelopes (request / response / notification / error).
- MCP DTOs: `initialize`/capabilities, `tools/list`, `tools/call`, content
  blocks, error objects.
- The **transport seam**: `IRpcTransport` (`SendRequest` + notifications +
  lifecycle). Core never knows whether it's talking over stdio or HTTP.
- Stdio newline framing + SSE event parser (operate on streams/strings handed
  in — no process or curl knowledge).
- JSON strategy: **typed DTOs** for stable envelopes; **`JObject`/`JToken`** at
  the open-ended boundaries (tool input-schemas, tool results).
- **Only** dependency is `Newtonsoft.Json`. No native tools, no reference to
  `GxPT.*` — not even logging (logging is injected as a delegate/interface).

### Mcp35.Server (builds on Core)
- Ergonomic tool registration: `name + description + input-schema + handler
  delegate`.
- The stdio dispatch loop (read framed requests on stdin, write responses on
  stdout).
- Shared **process-exec** and **curl-exec** helpers (curl path injected, never
  vendored here) so individual servers stay tiny.
- This is the high-value reusable deliverable; the four servers are its first
  consumers and its dogfooding.

### Host (GxPT `Services/Mcp/`)
- `McpHost` — owns the configured servers.
- `McpServerConnection` — one server: transport + negotiated capabilities +
  cached tools + lifecycle state machine.
- Transports:
  - `StdioTransport` — child process + std pipes.
  - `HttpTransport` — curl-per-POST, reusing OpenRouter's temp-file body, `-K`
    auth, `-N`, UTF-8 decode, cleanup; adds `Accept` +
    `MCP-Protocol-Version` headers, captures `Mcp-Session-Id` (via `-D
    <tempfile>`), and issues `DELETE` on teardown.
- `McpToolRegistry` — aggregates the full catalog across all servers and tracks
  the **revealed subset** exposed to the model.

### Mcp35.Servers.* (reference servers, net35 console exes)
- `Mcp35.Servers.Serper` — web search (Serper API; needs curl for TLS 1.2).
- `Mcp35.Servers.Files` — file read/list/write.
- `Mcp35.Servers.Git` — git status/diff/commit (uses `git.exe`).
- `Mcp35.Servers.Command` — arbitrary command execution (sharpest security
  edge; always-confirm).

---

## 4. Repo strategy: mono-repo first → split later

We develop Mcp35 **inside this repo** while its API churns (phases 1–5), then
split it into its own `mcp35` repo once stable (~after phase 5). During the
mono phase the host uses a **`ProjectReference`** to `Mcp35.Core`/`.Server`;
post-split it swaps to the **vendored-DLL `HintPath`** pattern (identical to
curl / DotNetZip / itextsharp today).

### The four seam rules (what keeps the split cheap)
The split stays a one-day job **only** if we develop as-if-separate from day one:

1. **One-way dependency, enforced.** `Mcp35.Core`/`.Server` must never
   reference `GxPT.*` — not Logger, not settings. Anything host-specific is
   injected (delegate/interface), the same pattern as `curlPath`.
2. **Neutral namespace from commit one** — `Mcp35.*` (no later rename).
3. **Library binary-pure / deps injected** (see §6) — this *is* the seam
   enforcement, not just distribution hygiene.
4. **Separate projects + separate test projects.** Project boundaries make the
   compiler *reject* an accidental reverse-reference.

### Split mechanics (later)
- `git subtree split` peels `Mcp35.*` into the new repo **with history
  preserved**.
- GxPT swaps `ProjectReference` → vendored `Mcp35.Core.dll` / `Mcp35.Server.dll`
  in `GxPT/lib/` via `HintPath`.
- Because the dependency was already one-way, that swap is the *only* code
  change.
- Newtonsoft travels **with Core** (vendored under `Mcp35.Core/lib/`), so it
  leaves with the library at split time.

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
├─ src/Mcp35.Server/            net35 lib   (ProjectRef → Mcp35.Core)
├─ src/Mcp35.Servers.Serper/    net35 Exe
├─ src/Mcp35.Servers.Files/     net35 Exe
├─ src/Mcp35.Servers.Git/       net35 Exe
└─ src/Mcp35.Servers.Command/   net35 Exe

(outside the .sln, run via dotnet test)
├─ GxPT.Tests/                  net48, linked-source  (existing)
├─ tests/Mcp35.Core.Tests/      net48, links Core sources, PackageRef Newtonsoft
└─ tests/Mcp35.Server.Tests/    net48, links Server sources + fake transport
```

- New net35 projects use **old-format MSBuild** (`ToolsVersion 3.5`, xmlns
  `.../2003`, `<TargetFrameworkVersion>v3.5`), matching `GxPT.csproj`.
- Host references the library via `<ProjectReference>` during the mono phase.
- `Mcp35.Servers.*` are reference consumers; at split they move to the `mcp35`
  repo. (Soft decision — see §15.)

---

## 6. Dependency policy

**Managed deps of the library: exactly one — `Newtonsoft.Json`** (net35 build,
vendored under `Mcp35.Core/lib/`). Core/Server reference only Newtonsoft + BCL.
No `System.Web.Extensions` (we're not using JavaScriptSerializer).

**Native tools are injected, never embedded.** curl is a *runtime external
process*, not a managed reference, and it does not flow transitively in the
HintPath world — so each deployable artifact vendors what **it** runs:

| Artifact | Native need | Source |
|----------|-------------|--------|
| GxPT host (HTTP → GitHub) | curl | **already vendored** in `GxPT/lib/`; reuse path injection (`OpenRouterClient(apiKey, curlPath)` pattern) |
| `Mcp35.Servers.Serper` | curl (TLS 1.2 to Serper API) | own `lib/curl.exe` next to the exe |
| `Mcp35.Servers.Git` | `git.exe` | injected path → PATH |
| `Mcp35.Servers.Files` / `.Command` | none | BCL only |
| `Mcp35.Core` / `.Server` (DLLs) | none | pure managed |

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
  since ~2015, and its only unique wins (no `System.Web.Extensions`, runs on
  Client Profile) don't apply here. Dropped.
- Config note: set Newtonsoft `MaxDepth` explicitly if MCP/JSON-Schema payloads
  nest beyond the default (64).

---

## 7. Tool-call loop + tool-search (host)

- `BuildRequestBody` gains a `tools` array; the chunk DTO/parser gains
  `tool_calls`.
- Loop: model emits a call → registry resolves the owning server → approval
  check (§9) → route via that connection's transport → append a `tool`-role
  result → re-invoke (bounded iterations).
- **Progressive disclosure**: only `tool_search` is exposed initially.
  `tool_search(query)` ranks against the full aggregated catalog (simple
  lexical match to start — net35-friendly); matches are revealed into `tools`
  on subsequent turns. This keeps token cost to a few tools across *all*
  servers, GitHub included.

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
  destructive tools: `Command`, git writes (commit/push), GitHub writes.
- `Mcp35.Servers.Command` is the sharpest edge: always-confirm + process
  isolation; treat all server/tool output as untrusted input at the parse
  boundary.

---

## 10. Transports

- **Stdio**: child process; newline-framed JSON-RPC on stdin/stdout; stderr
  surfaced to the host log.
- **HTTP (GitHub)**: Streamable HTTP, curl-per-POST. Reuses OpenRouter's
  temp-file request body, `-K` auth config, `-N`, UTF-8 decode, temp cleanup.
  Adds `Accept: application/json, text/event-stream`, `MCP-Protocol-Version`,
  captures `Mcp-Session-Id` from response headers (`-D <tempfile>`), and
  `DELETE`s the session on teardown. SSE responses are parsed by Core's SSE
  parser.

---

## 11. Build & CI

- The shipping **net35** assemblies need **VS2008 or msbuild + the v3.5
  targeting pack** to build — a stock modern SDK cannot (same constraint as the
  GxPT app today).
- But Core/Server are **pure logic (no WinForms)**, so CI coverage comes via
  the **net48 linked-source test projects** run with `dotnet test` — the proven
  `GxPT.Tests` trick. Those projects link the library `.cs` files and reference
  Newtonsoft (PackageReference resolves a net4x build).
- Extend `.github/workflows/tests.yml` to also run `Mcp35.Core.Tests` and
  `Mcp35.Server.Tests`.
- Transports get a fake/loopback `IRpcTransport` for host-side tests.

---

## 12. Testing strategy

- `Mcp35.Core.Tests` (net48, xunit, linked-source): JSON-RPC
  serialization/round-trips, field-presence (notification vs request, omitted
  `params`), SSE parsing, stdio framing, large-payload handling.
- `Mcp35.Server.Tests`: registration + dispatch loop against a fake transport.
- Host tests in `GxPT.Tests`: registry aggregation, tool-search ranking,
  tool-call loop with a loopback transport.

---

## 13. Phasing / roadmap

1. **Mcp35.Core** + Newtonsoft wiring + a depth spike on a real GitHub MCP
   `tools/list` and a large tool result (validate field-presence + big
   payloads).
2. **Mcp35.Server** + one trivial server (validate the stdio loop).
3. **Host `StdioTransport`** + consume that server.
4. **Tool-call loop** + OpenRouter `tools`/`tool_calls` — first real
   end-to-end invocation.
5. **Tool-search / progressive disclosure** (revealed subset).
   → *Split Mcp35 into its own repo here once the API is stable.*
6. **Approval tiers** (gate Command / git writes).
7. **Four servers**, riskiest last: Files → Serper → Git → Command.
8. **HTTP transport + GitHub MCP** (tool-search now tames its tool count).

---

## 14. Risks

- OpenRouter function-calling fidelity across models.
- Newtonsoft `MaxDepth` on deeply nested schemas (config, de-risk in phase 1).
- curl process-per-call latency.
- `Mcp35.Servers.Command` security surface.
- Mono-repo coupling creep defeating the future split — mitigated by the §4
  seam rules and project boundaries.

---

## 15. Open / soft decisions

- **Server home at split**: do all four `Mcp35.Servers.*` go to the `mcp35`
  repo, or do any GxPT-specific ones stay? Currently all four are generic →
  planned to move with the library.
- **Tool-search ranking**: starts as simple lexical match; revisit if recall is
  poor.
- **Where Newtonsoft net48 build comes from in tests**: PackageReference
  (assumed) vs HintPath to a vendored net48 dll.
