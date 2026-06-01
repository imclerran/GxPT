# First-party MCP servers — Implementation Spec (Phase 7)

**Status:** Design (implementation-level). Companion to `mcp-architecture.md`,
`mcp35-server-spec.md` (the SDK), and `mcp35-approval-spec.md` (the gate).
Realizes **phase 7** of the roadmap.
**Branch:** `claude/mcp-server-architecture-cJ088` · PR #22

The four bundled servers — **Files → Web → Git → Command** (built in that
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
| Web | `GXPT_WEB_SEARCH_KEY` | **required** (Tavily bearer token); absent → tool returns `Error` |
| Web | `GXPT_CURL_PATH` | curl exe (TLS 1.2); **required** (net35 can't TLS-1.2 natively) |
| Git | `GXPT_GIT_PATH` | `git` exe; default `"git"` (resolved on `PATH`) |
| Command | `GXPT_CMD_SHELL` | shell for `/c`; default `cmd.exe` (from `%ComSpec%`) |

Rules for the contract:
- A **missing required** secret (web key/curl) doesn't crash the server — the
  affected tool returns `ToolResults.Error("Web search API key not configured.")`.
  (The host normally won't even enable the web server without a key, §8 architecture, but
  the server stays defensive.)
- Config is read **once at startup** (`Config.FromEnvironment`), not per call.
- Secrets are never echoed into results, logs, or error text.

---

## 2. FilesMcpServer (server name `files`)

Read/list/search/write/edit/delete confined to a single **root** (`GXPT_WORKDIR`).
Tools map to the approval table: `read`/`list`/`search` ReadOnly,
`write`/`edit` Write(path-scoped), `delete` Destructive(path-scoped).

| Tool | Schema (required *) | Behavior |
|------|---------------------|----------|
| `read` | `path*`, `start_line?`, `end_line?`, `line_numbers?` | UTF-8 (BOM-aware) text of a file under root; optional 1-based inclusive line range and/or per-line numbering. |
| `list` | `path*`, `recursive?` | Directory entries: `{name,type,size}`; capped count. |
| `search` | `query*`, `path?`, `regex?`, `ignore_case?`, `glob?`, `max_results?` | Grep file contents under root → `{path,line,text}`; recursive, **streamed (any file size)**, skips binary. |
| `write` | `path*`, `content*`, `create_dirs?` | Atomic create/overwrite under root. |
| `edit` | `path*`, `old_string*`, `new_string*`, `replace_all?` | Targeted exact-span replacement; `old_string` must be unique unless `replace_all`. Atomic. |
| `delete` | `path*` | Delete a file or **empty** dir under root (Destructive). |

The **agentic** additions — `search`, `edit`, and `read`'s line-range/numbering
options — exist so the model can locate and surgically modify code without
rewriting whole files (fewer tokens, fewer clobbers). All share the sandbox and
binary sniff; `edit` reuses `write`'s atomic temp-then-move. The 1 MiB cap is a
**context** guard, so it applies only to operations that emit a whole file into
the model: a *whole-file* `read`. `search` and a *ranged* `read` **stream**
line-by-line — their output is bounded by `max_results` / the line range, not the
file size — so large files (where grep matters most) remain searchable and a
slice is always readable.

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

- `read`: a **whole-file** read enforces the **1 MiB** cap (over → `Error`,
  pointing the model at a line range) and detects binary (NUL byte in a head
  sample) → `Error("not a text file")`. A **ranged** read (`start_line`/`end_line`,
  1-based inclusive) **streams** instead: it works on files past the cap, with the
  *output* capped at 1 MiB (over → `Error`); a `start_line` past EOF → `Error`, an
  `end_line` past EOF just stops at the last line. `line_numbers` prefixes each
  returned line with a right-aligned number + tab. With no range and no numbering,
  content is returned **verbatim** (exact bytes preserved).
- `list`: cap entries (e.g. 1000) and note truncation; `recursive` bounded depth.
- `search`: walk the tree under `path` (default root) bounded by the same depth
  cap as `list`, **streaming** each file line-by-line (no per-file size cap);
  `regex` toggles literal-substring vs `.NET` regex (invalid regex → `Error`);
  `ignore_case` and a filename `glob` (`*`/`?`) filter; cap matches (`max_results`
  default 100, max 1000) and scanned files, noting `truncated`. Skips binary files
  silently; per-match line text is length-capped.
- `write`: write to a temp file in the same dir then `File.Move`/replace (atomic,
  crash-safe — mirrors `AppSettings`' settings.json write); `create_dirs` gates
  parent creation; refuse to overwrite a directory.
- `edit`: read (size cap + binary sniff), require `old_string` to occur exactly
  once (or `replace_all` to replace every occurrence), then write back via the same
  atomic temp-then-move as `write`; missing file / no match / non-unique match →
  `Error` (file left untouched). Returns the replacement count.
- `delete`: files and **empty** directories only (never recursive — keeps the
  blast radius of a single approved call bounded); missing path → `Error`.
- Symlink/junction escapes: on net35/Windows, resolve via `GetFullPath` and the
  boundary check; do **not** follow a path that resolves outside root.

## 3. WebSearchMcpServer (server name `web`)

Two tools backed by the **Tavily API**, over **curl** because net35's
`WebRequest`/`ServicePointManager` can't reliably negotiate TLS 1.2. Reuses
**`Mcp35.Core`'s `CurlRunner`** (the shared curl-exec helper lives in Core
precisely so this server and the client's `HttpTransport` share it — D9,
architecture §3).

| Tool | Approval | Schema (required *) | Behavior |
|------|----------|---------------------|----------|
| `search` (→ `web__search`) | ReadOnly | `query*`, `max_results?` (default 5, cap 20), `topic?`, `search_depth?`, `chunks_per_source?`, `include_answer?`, `include_raw_content?`, `time_range?`, `start_date?`, `end_date?`, `country?`, `include_domains?`, `exclude_domains?` | `POST /search`; condensed results + optional answer. |
| `extract` (→ `web__extract`) | **Write (confirm each call)** | `urls*` (string or array), `extract_depth?`, `format?`, `include_images?` | `POST /extract`; fetch and return full page content for the given URLs. |

`extract` is gated as **Write** (not ReadOnly): it fetches arbitrary external
pages — more network/token cost, and it pulls in whatever content the page
serves — so each call is confirmed (`mcp35-approval-spec.md`).

- Auth: `Authorization: Bearer <GXPT_WEB_SEARCH_KEY>`, passed to curl via a
  **`-K` config file**, never the command line (same discipline as
  `HttpTransport`/architecture §8). Content type `application/json`.
- Endpoints: `https://api.tavily.com/search` and `https://api.tavily.com/extract`.
- `search` response: parse with Newtonsoft; project `results[]` →
  `{title, url, content, score?, raw_content?}` plus the synthesized `answer`
  when `include_answer` was set. `extract` response: `results[] {url,
  raw_content, images?}` plus any `failed_results`. Both return via
  `ToolResults.Json(structured)` (structuredContent + a readable text rendering).
- Failure modes → `Error` (never throw): missing key/curl, curl non-zero/timeout,
  non-2xx status, empty/unparseable JSON, empty query / no URLs. The raw API key
  never appears in any message.

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
  (temp files); Files/Web are stateless.
- **First-party classification is hardcoded host-side** — these servers do **not**
  emit MCP annotations expecting to be trusted; the host's approval table is
  authoritative for them (`mcp35-approval-spec.md` §2). Annotations, if present,
  are ignored for first-party.

---

## 7. Project & test layout

```
servers/
├─ FilesMcpServer/      net35 Exe  (ProjectRef → Mcp35.Server)
├─ WebSearchMcpServer/  net35 Exe  (also uses Mcp35.Core.CurlRunner)
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
3. **Web** — happy path parses `results[]` into structured+text (curl stubbed
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

## 9. Resolved decisions

1. **Files write/delete granularity** — **resolved**: `write` is path-scoped
   (Argument(`path`)); `delete` handles files + **empty** dirs only (no recursive
   delete) — the smallest blast radius per approved call. Recursive delete would
   need its own louder gate and isn't worth the risk in phase 7.
2. **Command shell vs argv** — **resolved (confirmed)**: run via `cmd.exe /c
   <command>` — a shell line matching what the user approved verbatim (pipes /
   redirects / env-expansion work as users expect). Containment is the host gate
   + working-dir + timeout, not string filtering (§5).
3. **Read range/paging** — **resolved**: `read` is whole-file with a size cap +
   `Error` over it; `offset`/`limit` are a later enhancement if a real need
   appears.
4. **Web result shape** — **resolved**: condense to `results[] {title, url,
   content, score?}` + optional synthesized `answer` (via `ToolResults.Json`) —
   keeps context cost down vs. passing raw Tavily JSON through. `extract` returns
   `results[] {url, raw_content}` + `failed_results`.
