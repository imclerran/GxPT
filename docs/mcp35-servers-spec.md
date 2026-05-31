# First-party MCP servers — Implementation Spec (Phase 7)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`,
`mcp35-server-spec.md` (the SDK), and `mcp35-approval-spec.md` (the gate).
Realizes **phase 7** of the roadmap.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

The four bundled servers — **Files → Serper → Git → Command** (built in that
order, *riskiest last*). Each is an independent net35 console exe consuming the
`Mcp35.Server` SDK (D10/D13); none references `GxPT.*`. They are the SDK's first
real consumers and its dogfooding.

---

## 0. Scope & constraints

- **In scope:** the four servers' tool sets, input schemas, handler behavior,
  sandboxing/escaping each server owns, and the **host↔server launch contract**
  (env vars the host injects, §1).
- **Out of scope:** the launch *plumbing* (host hardcodes the exe path + env —
  architecture §8, D15), the approval gate (host-side — `mcp35-approval-spec.md`),
  resources/prompts, dynamic tool sets (all four are **static** lists).
- Same constraints as the SDK: **net35**, no `async`/`Task`/`dynamic`, Newtonsoft
  only, **no `GxPT.*`**. Each is a `net35 Exe`, `ProjectReference → Mcp35.Server`.
- **Division of trust (architecture §9):** the *host* decides whether a call is
  allowed (tiers + argument-scoped rules); the *server* is responsible for
  **safe construction** — sandbox confinement, argv escaping, timeouts — so that
  even an allowed call can't escape its lane. The gate and the sandbox are
  independent layers; neither trusts the other to be the only defense.

---

## 1. Common shape & the launch contract

Every server is the same five lines around a different tool set:

```csharp
static void Main()
{
    var log = new StdErrLogSink();                         // never stdout (SDK §5)
    var s   = new McpServer(new Implementation { name = "files", version = "1.0" }, log);
    RegisterTools(s, Config.FromEnvironment(log));         // §2–5
    s.Run();                                               // blocks until stdin EOF
}
```

**Config arrives via environment variables** the host injects when it spawns the
child (`StdioTransport`, architecture §8). This is the entire host↔server
contract — keep it small and namespaced `GXPT_*`:

| Server | Env var | Meaning / default |
|--------|---------|-------------------|
| *all* | `GXPT_WORKDIR` | sandbox / working root; default = process `CurrentDirectory` |
| Files | (uses `GXPT_WORKDIR` as its root) | — |
| Serper | `GXPT_SERPER_KEY` | **required**; absent → tool returns `Error` |
| Serper | `GXPT_CURL_PATH` | curl exe (TLS 1.2); **required** (net35 can't TLS-1.2 natively) |
| Git | `GXPT_GIT_PATH` | `git` exe; default `"git"` (resolved on `PATH`) |
| Command | `GXPT_CMD_SHELL` | shell for `/c`; default `cmd.exe` (from `%ComSpec%`) |

Rules for the contract:
- A **missing required** secret (Serper key/curl) doesn't crash the server — the
  affected tool returns `ToolResults.Error("Serper API key not configured.")`.
  (The host normally won't even enable Serper without a key, §8 architecture, but
  the server stays defensive.)
- Config is read **once at startup** (`Config.FromEnvironment`), not per call.
- Secrets are never echoed into results, logs, or error text.

---

## 2. FilesMcpServer (server name `files`)

Read/list/write/delete confined to a single **root** (`GXPT_WORKDIR`). Tools map
to the approval table: `read`/`list` ReadOnly, `write` Write(path-scoped),
`delete` Destructive(path-scoped).

| Tool | Schema (required *) | Behavior |
|------|---------------------|----------|
| `read` | `path*` | UTF-8 (BOM-aware) text of a file under root. |
| `list` | `path*`, `recursive?` | Directory entries: `{name,type,size}`; capped count. |
| `write` | `path*`, `content*`, `create_dirs?` | Atomic create/overwrite under root. |
| `delete` | `path*` | Delete a file or **empty** dir under root (Destructive). |

**Sandbox — the one rule that matters here:** every `path` is resolved against
the root and must stay inside it.

```csharp
string Resolve(string root, string rel) {
    if (Path.IsPathRooted(rel)) Reject("absolute paths are not allowed");
    var full = Path.GetFullPath(Path.Combine(root, rel));   // canonicalize . / ..
    if (!IsWithin(root, full)) Reject("path escapes the workspace root");
    return full;
}
// IsWithin uses a directory-boundary check (root + Path.DirectorySeparatorChar),
// NOT a bare StartsWith — so "/root" does not match "/root-evil".
```

- `read`: enforce a **max size** (e.g. 1 MiB); over → `Error` (range reads are a
  later enhancement). Detect binary (NUL byte in a head sample) → `Error("not a
  text file")` rather than emitting garbage into the model context.
- `list`: cap entries (e.g. 1000) and note truncation; `recursive` bounded depth.
- `write`: write to a temp file in the same dir then `File.Move`/replace (atomic,
  crash-safe — mirrors `AppSettings`' settings.json write); `create_dirs` gates
  parent creation; refuse to overwrite a directory.
- `delete`: files and **empty** directories only (never recursive — keeps the
  blast radius of a single approved call bounded); missing path → `Error`.
- Symlink/junction escapes: on net35/Windows, resolve via `GetFullPath` and the
  boundary check; do **not** follow a path that resolves outside root.

## 3. SerperMcpServer (server name `serper`)

One tool, `search` (→ `serper__search`, ReadOnly). Web search via
`google.serper.dev`, over **curl** because net35's `WebRequest`/`ServicePointManager`
can't reliably negotiate TLS 1.2. Reuses **`Mcp35.Core`'s `CurlRunner`** (the
shared curl-exec helper lives in Core precisely so the Serper server and the
client's `HttpTransport` share it — D9, architecture §3).

| Tool | Schema | Behavior |
|------|--------|----------|
| `search` | `query*`, `num?` (default 10, cap ~20), `gl?`, `hl?` | POST search; return condensed organic results. |

- Request: `POST https://google.serper.dev/search`, header `X-API-KEY:
  <GXPT_SERPER_KEY>` (passed to curl via **`-K` config file**, never the command
  line — same discipline as `HttpTransport`/architecture §8), JSON body
  `{ "q": query, "num": n, "gl": …, "hl": … }`.
- Response: parse with Newtonsoft; project `organic[]` → `{title, link, snippet}`
  plus `answerBox`/`knowledgeGraph` if present. Return via
  `ToolResults.Json(structured)` (structuredContent + a readable text rendering),
  so the model gets both a clean list and prose.
- Failure modes → `Error` (never throw): missing key, curl non-zero/timeout,
  non-200 status, unparseable JSON. The raw API key never appears in any message.

## 4. GitMcpServer (server name `git`)

git operations against the repo at `GXPT_WORKDIR`, via the SDK's `ProcessRunner`
driving `GXPT_GIT_PATH`. Tools map to the approval table: `status`/`diff`/`log`
ReadOnly, `commit` Write, `push` Destructive(`Scope=None`).

| Tool | Schema | git invocation |
|------|--------|----------------|
| `status` | — | `status --porcelain=v1 -b` |
| `diff` | `staged?`, `path?` | `diff [--staged] [-- <path>]` |
| `log` | `max?` (default 20) | `log -n <max> --pretty=…` |
| `commit` | `message*`, `all?` | stage (`add -A` if `all`) then `commit -F -` |
| `push` | `remote?`, `branch?` | `push [<remote> [<branch>]]` |

**Argv safety — this server builds command lines, so it owns escaping
(architecture §9):**
- Construct args as a **list of discrete tokens**, then join with a single
  Windows-correct quoter (`ProcessRunner.Arguments` is pre-escaped by the caller
  — SDK §6). No user/model string is ever concatenated into a shell line.
- The commit **message is passed via stdin** (`git commit -F -`,
  `ProcessRequest.StdinText`), never as a `-m "<message>"` argument — this
  removes message-content quoting as an injection surface entirely.
- `path` for `diff` is placed **after `--`** so it can't be read as a flag.
- Every call sets `WorkingDirectory = GXPT_WORKDIR` and a `TimeoutMs`; a non-zero
  exit or timeout → `Error` carrying captured stderr (trimmed).
- `push` is Destructive and host-gated every time (`Scope=None`); the server
  still just runs it — the *server* never decides policy, only executes safely.

## 5. CommandMcpServer (server name `command`) — the sharpest edge

One tool, `run` (→ `command__run`, Destructive, argument-scoped). Executes an
arbitrary command line. This server has **no allowlist of its own** — that's the
host's argument-scoped gate (base+subcommand / exact, `mcp35-approval-spec.md`).
The server's job is to execute the *already-approved* command **safely and
observably**.

| Tool | Schema | Behavior |
|------|--------|----------|
| `run` | `command*`, `timeout_ms?` | Run `command` via the shell; capture stdout/stderr/exit. |

- Execution: `ProcessRunner` with `FileName = GXPT_CMD_SHELL` (`cmd.exe`) and
  `Arguments = "/c " + command` — the command string is handed to the shell as
  written (the shell, not this server, parses it; the host already showed the
  user the **exact** string at the gate).
- `WorkingDirectory = GXPT_WORKDIR`; **always** a `TimeoutMs` (default + caller
  cap) with **process-tree kill** on overrun (SDK §6) — a hung/forking command
  can't wedge the server.
- Result: `ToolResults.Json({ exitCode, stdout, stderr, timedOut })` (truncated
  to a sane cap with a "truncated" flag) so the model sees the outcome verbatim.
- `CreateNoWindow`, no inherited interactive handles, stdout captured separately
  from the JSON-RPC stream (SDK §5) — a sub-process printing to stdout can't
  corrupt framing.
- The server treats **all** of `command` as opaque and never tries to "sanitize"
  it (sanitizing would give a false sense of safety); containment is the gate +
  the working-dir + the timeout, not string filtering.

---

## 6. Cross-cutting behavior

- **Static tool sets** — `listChanged = false`; the host's names-manifest /
  `reveal_tools` machinery (phase 5) handles surfacing them to the model.
- **Errors are values** — handlers return `ToolResults.Error(...)`; a thrown
  handler is contained by the runtime → `isError` (SDK §3–4). No server ever
  exits on a tool failure.
- **stdout = JSON-RPC only** — all diagnostics to stderr via `StdErrLogSink`; the
  host drains it into `GxPT.Logger` (SDK §5).
- **Graceful shutdown** — stdin EOF → `OnShutdown` hooks → flush → exit 0. Git
  and Command register cleanup hooks if they hold any transient state
  (temp files); Files/Serper are stateless.
- **First-party classification is hardcoded host-side** — these servers do **not**
  emit MCP annotations expecting to be trusted; the host's approval table is
  authoritative for them (`mcp35-approval-spec.md` §2). Annotations, if present,
  are ignored for first-party.

---

## 7. Project & test layout

```
servers/
├─ FilesMcpServer/      net35 Exe  (ProjectRef → Mcp35.Server)
├─ SerperMcpServer/     net35 Exe  (also uses Mcp35.Core.CurlRunner)
├─ GitMcpServer/        net35 Exe
└─ CommandMcpServer/    net35 Exe
```

Each is buildable in `GxPT.sln` (VS2008 format) and has a sibling **net48
linked-source** test project (kept out of the `.sln`, run via `dotnet test` —
architecture §5), driving the server's `Run(stdin, stdout)` with scripted
in-memory streams (same harness as `Mcp35.Server.Tests`).

## 8. Testing — phase-7 exit criteria

Per server, over the scripted-stream harness:
1. **Listing** — `tools/list` returns exactly the documented tools with intact
   `inputSchema`.
2. **Files sandbox** — `..`, absolute paths, and `/root` vs `/root-evil` boundary
   tricks are all rejected; in-root read/list/write/delete round-trip; oversize
   and binary reads → `Error`; write is atomic; delete refuses non-empty dirs.
3. **Serper** — happy path parses `organic[]` into structured+text (curl stubbed
   via injected `GXPT_CURL_PATH` pointing at a fake); missing key, curl failure,
   non-200, bad JSON → `Error`; key never leaks into output.
4. **Git** — args built as discrete tokens (assert the `ProcessRequest`);
   commit message goes via **stdin**, not argv; `diff` path sits after `--`;
   non-zero exit/timeout → `Error` with stderr (git stubbed via `GXPT_GIT_PATH`).
5. **Command** — runs via `GXPT_CMD_SHELL /c`; timeout triggers a tree-kill and
   `timedOut=true`; stdout/stderr/exit captured; large output truncated; stdout
   capture never touches the JSON-RPC stream.
6. **Lifecycle** — stdin EOF → clean exit 0; a throwing handler stays alive as
   `isError`.

Passing this leaves only **phase 8** (HttpTransport + GitHub MCP) before the
feature is end-to-end.

---

## 9. Decisions to confirm

1. **Files write/delete granularity** — *recommended*: `write` is path-scoped and
   `delete` is files + **empty** dirs only (no recursive delete) — the smallest
   blast radius per approved call. (Looser alt: allow recursive `delete` with a
   louder gate.)
2. **Command shell vs argv** — *recommended*: run via `cmd.exe /c <command>` (a
   shell line, matching what the user approved verbatim). (Stricter alt: require
   the model to pass an argv array and **never** invoke a shell — safer against
   shell metacharacters, but breaks pipes/redirects users expect.)
3. **Read range/paging** — *recommended*: phase-7 `read` is whole-file with a size
   cap + `Error` over it; add `offset`/`limit` later if needed. (Alt: ship range
   reads now.)
4. **Serper result shape** — *recommended*: condense to `organic[] {title, link,
   snippet}` + answer/knowledge boxes. (Alt: pass the raw Serper JSON through —
   bigger context cost, more for the model to parse.)
