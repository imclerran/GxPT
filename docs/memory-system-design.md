# Memory System — Design Proposal

**Status:** Design (proposal). No implementation yet.
**Branch:** `claude/sleepy-pascal-7Tiuo`
**Last updated:** 2026-06-04

A persistent, project-scoped memory for GxPT: the model can record durable facts
about a workspace and recall them in later conversations. Built as a first-party
MCP server for the tool surface, plus host-side injection of a light index.

---

## 1. Scope & goals

- **Project-scoped only.** Memory lives in a `.gxpt/` directory under the
  conversation's working folder. No global/cross-project tier — memory is
  available **only when the conversation has a working directory**.
- **Light always-on context + detail on demand.** A small primary index is
  injected every request; richer notes live in separate files the model reads
  by name when relevant (mirrors the `reveal_tools` progressive-disclosure model).
- **One tool pathway.** Reuse the existing MCP host, manifest, approval tiers,
  and routing — no parallel tool channel.

### Non-goals (initially)
- Global / user-level memory (`%AppData%`); cross-conversation sync; automatic
  summarization or eviction beyond a soft size cap.

---

## 2. Decision ledger

| # | Decision | Rationale |
|---|----------|-----------|
| M1 | **Standalone `MemoryMcpServer`** (stdio, workdir-scoped), not a loopback/in-process server | Reuses the tested `StdioTransport` + `McpServer` loop; process isolation + per-call timeout; just one more `NewBuiltIn(...)`. Disk is the source of truth, so in-process state sharing buys nothing. |
| M2 | **Host owns injection; server owns writes** | One-way contract over a known path (`.gxpt/`), exactly like the `GXPT_WORKDIR` convention. Host reads the primary file each request; the server is the only writer. |
| M3 | **Memory-semantic tools, not raw file I/O** | The server keeps the primary index consistent; the model supplies content, not file edits. Raw read/write would just re-skin `FilesMcpServer`. |
| M4 | **Name = handle, summary = description** | Each index entry is `name: summary` (+ optional `→ detail.md`). The caller supplies the **name** (a handle, max 5 words); the **summary** is a single line of prose and never a key. |
| M5 | **Single enable flag, surfaced in the main settings tab** | Memory is a feature (with context cost), not server plumbing. One `MemoryEnabled` bool gates **both** server launch and injection, so they can't desync. |
| M6 | **No memory text in the constant `AgentSystemPrompt`** | All memory framing lives inside the omittable injected block, so the OFF state leaves zero trace — no phantom capability. |

---

## 3. On-disk layout (`<workdir>/.gxpt/`)

```
.gxpt/
  memory.md        # primary index: light, injected every request
  <name>.md        # detail files, read on demand
  .gitignore       # default "*" — personal memory not committed unless opted in
```

`memory.md` is a flat list of entries:

```
- auth-flow: JWT in httpOnly cookies; refresh via POST /auth/refresh  → auth-flow.md
- db-conventions: snake_case tables, soft-delete via deleted_at
- build-quirks: net35 servers built by the solution, deployed beside GxPT.exe
```

Each entry is one line: a `name` (caller-supplied handle, **max 5 words**) and a
**single-line** `summary`. Soft size cap on `memory.md`; the server warns /
suggests compaction when exceeded.

---

## 4. Tool surface (`MemoryMcpServer`)

- `remember(name, summary, detail?)` — appends an index entry; if `detail` given,
  writes `<name>.md` and links it. `name` is **required** (max 5 words), `summary`
  is a **single line**; the server validates both and rejects a name that collides
  with an existing entry. *(Write tier — remember-eligible.)*
- `read_memory(name)` — returns a detail file's contents. *(ReadOnly.)*
- `update_memory(name, summary?, detail?)` — revise an entry/detail. *(Write.)*
- `forget(name)` — remove an entry and its detail file. *(Write.)*

Because `memory.md` is in context every turn, the names are always in front of
the model — it addresses entries by reading the name beside the summary it
recognizes. Writes are atomic (temp file + rename). The server is sandboxed to
`.gxpt/` (reuse `PathSandbox`).

---

## 5. Context injection (host-side)

When memory is enabled and the conversation has a workdir, the orchestrator emits
`memory.md` as an **ephemeral, non-persisted system message**, rebuilt from disk
each request — the same pattern as the names manifest. Benefits: edits take effect
next turn; exports / message-editing / replay stay clean; no double-storage.

Order ephemeral system messages stable → volatile for prompt-cache reuse:

```
[system] AgentSystemPrompt        (constant)
[system] memory.md                (changes rarely within a turn)
[system] names manifest           (rebuilt each request)
...history
```

---

## 6. Enable / disable

A single `MemoryEnabled` bool, stored with the other first-party toggles in
`BuiltInOptions` (so `McpHost` launches the server through the normal path), but
its **toggle is rendered in the main settings tab**, not the MCP/tools tab. Two
consumers read the one value: server launch and injection gate.

- **ON:** server launched + registered (manifest lists its tools), `memory.md`
  injected.
- **OFF:** server not launched, no memory tools in the manifest, no injection —
  the model sees no memory tools and makes no memory claims.

Memory is not surfaced as an independent toggle in the MCP tab (avoids a second,
desync-prone switch); at most a read-only "managed in Settings" indicator.

---

## 7. Open questions

- Tier of `forget` — keep as Write, or treat deletion as Destructive (always
  confirm)?
- Soft cap value for `memory.md`, and whether compaction is model-driven or a
  server nudge.
- Whether to seed `.gxpt/.gitignore` automatically or prompt on first write.
