# Skills System — Design Proposal

**Status:** Design (proposal). No implementation yet.
**Branch:** `claude/inspiring-fermat-zHNpw`
**Last updated:** 2026-06-08

On-demand, progressive-disclosure **capabilities** for GxPT: a skill is a folder
of markdown instructions (plus optional bundled files/scripts) that the model can
pull into context when a task calls for it. Skills are *know-how and procedure*
("how to do X well here"), distinct from MCP tools, which are *effects* ("read a
file", "run a command"). They compose: a skill body can tell the model to
`reveal_tools` and call specific tools in a specific order.

The whole design rides GxPT's existing principle — **progressive disclosure to
bound token cost** — which the MCP layer already implements for tools via a
names-only manifest + `reveal_tools` (`mcp-architecture.md` §7). Skills are the
same idea one level up.

---

## 1. Scope & goals

- **Two skill sources from day one: bundled + project.** First-party skills ship
  beside `GxPT.exe`; project skills live under the conversation's working folder.
  (User-global `%AppData%` skills are a later addition, not in the initial cut.)
- **Three disclosure levels.** Only the skill **name + one-line description** is
  always in context (the manifest); the **`SKILL.md` body** loads on demand; the
  **bundled files/scripts** are read/run only when used.
- **Host-native catalog, sandboxed execution.** Discovery, `open_skill`, and asset
  reads (`read_skill_file`) live in the GxPT host (host-synthesized meta-tools, like
  `reveal_tools`). Running a skill's bundled **script** goes through a dedicated,
  sandboxed `SkillsMcpServer` (the skills analogue of `CommandMcpServer`),
  always-confirm.
- **Skills can ship runnable scripts.** A bundled `.bat`/`.cmd` entry (which may wrap
  a sibling `.py`/`.ps1`/`.exe`) is invoked **by handle** — `(slug, relpath)`, never
  an absolute path — and runs in the **workspace**, with its own folder reachable
  **read-only** for assets.
- **Two trigger paths into one catalog.** The model can open a skill autonomously
  (`open_skill`), and the user can drive it explicitly with a slash command. Both
  resolve against the same `SkillCatalog`.
- **No settings-page UI.** Skill management (global + per-skill on/off) is done with
  slash commands, scoped per conversation. Bundled skills "just work" by default.

### Non-goals (initially)
- User-global (`%AppData%/GxPT/skills/`) skills; a skills-authoring tool surface
  (the model writing skills — that would mirror `MemoryMcpServer`'s writer role);
  a marketplace / remote skill install; per-skill settings UI.

---

## 2. Decision ledger

| # | Decision | Rationale |
|---|----------|-----------|
| S1 | **Catalog & reads are host-synthesized meta-tools** (`open_skill`, `read_skill_file`), not a server; **only execution gets a server** (S11) | Discovery and asset reads are just disk reads, so a stdio process buys nothing — they ride the host registry like `reveal_tools`, never routed to a connection (D11). Spawning a *script* is the one concern that genuinely needs process isolation + a per-call timeout, so it (and only it) is a server. |
| S2 | **Bundled + project scope at launch** (`<exe>/skills/` and `<workdir>/.gxpt/skills/`); user-global deferred | Bundled "starter" skills are the main draw, so unlike memory (project-only by choice) skills lead with bundled. Project skills reuse the `.gxpt/` convention the memory system established. |
| S3 | **Model-initiated *and* slash-command triggers**, over one catalog | `open_skill` matches GxPT's `reveal_tools` precedent (the model picks from the manifest mid-task); the slash command gives the user a deliberate "run this now" entry point. Both pre-/open the same `SKILL.md`, so there is one load path, not two. |
| S4 | **`SKILL.md` = minimal frontmatter + markdown body**, parsed by a hand-rolled reader | net35 has no YAML parser and the repo keeps exactly one JSON lib (Newtonsoft, D3). A ~20-line `--- … ---` reader for `name`/`description` keeps authoring familiar to Claude Code skills without a new dependency or a JSON sidecar. |
| S5 | **Folder name = slug = handle**; reuse the memory system's kebab-case `Slug` | No name→path map needed (memory M4). `<workdir>/.gxpt/skills/<slug>/SKILL.md`; the slug is what appears in the manifest, what `open_skill` takes, and what the slash command types. |
| S6 | **No skills UI in `SettingsForm`; enablement is driven entirely by slash commands** at two scopes — **global default** and **this conversation** — default **all-on** | Skills are a discoverable capability, not a privacy-sensitive opt-in like memory, so they default on and need no settings toggle. Global gives an app-wide default; per-conversation matches the `Zdr`/`WorkingDir` grain (different tabs, different skill sets). |
| S10 | **Conversation overrides the global default** (tri-state per skill: *inherit / on / off*); global state lives in a dedicated `%AppData%/GxPT/skills.json`, conversation overrides in the conversation JSON | A plain disabled-set can't express "force-on over a global-off", so the conversation layer is tri-state, not a set. A dedicated file keeps global state out of `settings.json` and the settings form (S6) while still persisting it — matching the repo's `recent-workdirs.json` / `mcp.json` dedicated-file pattern. |
| S7 | **`open_skill` returns the body as the tool result** (not a separate ephemeral injection) | Returning through the normal tool-loop lands the body in history naturally and keeps it available for the rest of the task — no new injection channel. The always-on **manifest** still rides as an ephemeral system message (S8). |
| S8 | **Manifest injected as an ephemeral system message**, in the stable→volatile stack | Same treatment as the names manifest and the memory block: rebuilt each request from the live catalog, never persisted. Cheap (names + one-liners), so it sits in front of history every turn when skills are enabled for the conversation. |
| S9 | **Skill scripts run via a dedicated `run_skill_script(slug, relpath, args[])` tool, *not* `command__run`** | A skill should not make the model compose an arbitrary command or name a path in the user's Program Files. The handle-based tool shrinks the exec surface from "any command string" to "the declared entry of a named skill + literal args" — smaller, auditable, still gated (Destructive/always-confirm). |
| S11 | **Execution lives in a first-party `SkillsMcpServer`** (stdio, launched with the skill roots in its env), reusing `Mcp35.Server`'s process-exec helper | **Decisive reason:** the process-spawn + cmd.exe arg-quoting helper (the `^`/`%` footgun) lives in `Mcp35.Server`, and the host references only `Mcp35.Client` — one-way seam (D8) — so a host meta-tool can't reuse it without breaking the boundary or duplicating it; a server consumes it like the other six do. Plus **process isolation** (arbitrary author code stays out of the WinForms UI process; a wedged server is killable while the UI stays responsive), the existing **per-call timeout + Destructive/remember approval path** for server tools (S15), and the **M1 precedent** (`MemoryMcpServer` chose a server for *weaker* reasons). It's the skills analogue of `CommandMcpServer`. Catalog/reads stay host-native because they're in-process-safe disk reads with no child process, timeout, or quoting hazard — nothing to isolate (S1). |
| S12 | **Entry points are `.bat`/`.cmd` only**; `.py`/`.ps1`/`.exe` are reached *through* the batch via `%~dp0` | One uniform, validatable entry type; the author owns interpreter selection (and graceful failure when it's absent). `%~dp0` (the batch's own dir at runtime) locates bundled siblings install-path-independently, so nothing hardcodes where `<exe>` landed. A user-authored skill with Python/PS installed ships a `.bat` wrapper calling its sibling script. |
| S13 | **Assets are read by handle via `read_skill_file(slug, relpath)`** (ReadOnly), not `files__*` | `files__*` is hard-sandboxed to the workspace and rejects absolute paths, so it can't reach a bundled skill's files. A relative-handle read tool resolves `slug`+`relpath` host-side (within the skill root), works for bundled *and* project skills uniformly, and auto-allows by the ReadOnly tier — mirroring `read_memory`. |
| S14 | **cwd is always the workspace; the skill dir is a read-only asset source, never a working/output dir** | Skills operate on and write to the user's project, not their own install location (which, for bundled skills, is often non-writable Program Files anyway). `%~dp0` / `GXPT_SKILL_DIR` locate read-only assets; all output goes to cwd. `run_skill_script` therefore requires a workspace (like `command__run`); a workspace-less self-contained skill would use a temp scratch cwd — deferred, never the skill dir. |
| S15 | **`run_skill_script` is Destructive but remember-eligible by exact `(slug, relpath)`**; pipelines live *inside* the script | Because the surface is a fixed declared entry (not an arbitrary string), a remembered approval is narrow and auditable — *less* friction than `command__run` while *more* locked down. Args are shown each run but aren't part of the remembered grain. Output filtering belongs inside the batch (`… | findstr …`); a shell pipeline in the tool call would re-import the arbitrary-shell surface the tool exists to remove. |

---

## 3. On-disk layout

### Bundled (first-party, ships beside the exe)
```
<GxPT.exe dir>/skills/
  <slug>/
    SKILL.md             # frontmatter + body
    scripts/
      gen.bat            # entry point (.bat/.cmd only) — may wrap a sibling script
      gen.py             # reached via %~dp0, never invoked directly
    template.md          # read on demand via read_skill_file
    examples/
```
Deployed by the same `AfterBuild` copy that places `mcp-servers\` (and seeded into
the setup `.vdproj`), so bundled skills travel with the install.

### Project (scoped to the conversation's working folder)
```
<workdir>/.gxpt/skills/
  <slug>/
    SKILL.md
    <bundled files…>
```
Reuses the memory system's `.gxpt/` home. Project skills **shadow** a bundled skill
of the same slug (precedence **project > bundled**).

### `SKILL.md` format
```markdown
---
name: Release Notes
description: Draft release notes from the git log since the last tag.
---

1. Use the git tools to list commits since the most recent tag.
2. Group by type (feat/fix/docs/…), one short summary line per group.
3. Fill in `./template.md`; see `./examples/` for tone.
```
- Frontmatter is a leading `--- … ---` block of `key: value` lines; only `name`
  and `description` are read initially (unknown keys ignored, forward-compatible).
- `description` is a **single line** — it is what the model sees in the manifest, so
  it should read as "use this when…".
- The body and any sibling files are Level-2/3 content, paid for only on open.
- **Reference bundled assets by *relative* path only — never absolute.** The install
  dir varies per machine, so the body names assets relatively (`scripts/gen.bat`,
  `template.md`); the host resolves them to this machine's location on open (§5), and
  scripts find their own siblings via `%~dp0` at runtime.

---

## 4. Disclosure levels

| Level | Content | When it enters context | Cost |
|-------|---------|------------------------|------|
| 1 | `slug` + `description` (the **manifest**) | every request, while skills are enabled for the conversation (S8) | tiny |
| 2 | the **`SKILL.md` body** | when `open_skill(slug)` is called (by model or slash) (S7) | medium |
| 3 | **bundled files/scripts** | files read via `read_skill_file` (S13); scripts run via `run_skill_script` (S9/S11) | paid on use |

---

## 5. Host pieces

### Catalog & reads (host-native, `Services/Skills/`)

```
McpChatOrchestrator (RunTurn loop)
  ├─ ephemeral system stack (stable → volatile):
  │     AgentSystemPrompt
  │     WorkspaceSystemMessage(workdir)
  │     [memory.md block]
  │     [SKILLS MANIFEST]          ← new (S8): "slug — description" per enabled skill
  │     names manifest
  │     ...history
  │
  └─ exposed tools: reveal_tools, open_skill(names[]), read_skill_file(slug, relpath),
                    run_skill_script(slug, relpath, args[]), …revealed tools
```

- **`SkillCatalog`** — scans bundled + project skill folders, parses each frontmatter
  into `(slug, description, path)`, applies project-over-bundled shadowing, and
  produces `SkillsManifestSystemMessage(enabledSet)` and `slug → path` resolution.
  Pure logic (no WinForms) → net48 linked-source tests.
- **`open_skill(names[])`** (host-synthesized, like `reveal_tools`) — returns, per
  named skill: the **`SKILL.md` body** (authored with *relative* asset paths), the
  **resolved absolute skill directory on this machine**, and a **listing** of bundled
  assets (relative path → resolved absolute). The author writes once, portably; the
  host supplies the machine-specific location, so no absolute path is ever baked into
  the markdown. Batch (plural) to resolve ambiguity by opening candidates.
- **`read_skill_file(slug, relpath)`** (host-synthesized, **ReadOnly**, S13) — reads a
  Level-3 asset by relative handle, resolved within the skill root (`PathSandbox`
  semantics against that root: no absolute, no `..` escape). Works for bundled and
  project skills alike; auto-allows by tier like `read_memory`. Keeps `open_skill`'s
  result a *listing*, so big templates aren't dumped into context.
- **Optional LRU cap** on how many full bodies persist, reusing the `reveal_tools`
  reveal-cap machinery — only if skill bodies start to crowd context. Not needed
  initially.

### Execution — `SkillsMcpServer` (S11)

A first-party stdio server, launched with the skill roots (bundled dir +
`<workdir>/.gxpt/skills`) in its environment, exposing one tool:

```
run_skill_script(slug, relpath, args[])
```

**Locking, before anything runs:**
1. **slug → skill root** via the catalog; reject unknown or disabled skills.
2. **relpath → file**, resolved within the root (`PathSandbox`: no absolute, no `..`
   escape, no drive/UNC). Must stay inside the root.
3. **Extension allowlist:** `.bat`/`.cmd` only (S12). (Optionally require entries
   under a conventional `scripts/` subdir.)
4. **args** passed as literal argv tokens (`%1..%N`), each quoted host-side — no shell
   metacharacters from the model are honored. *(cmd.exe + batch arg quoting is a known
   footgun — `^`/`%` handling — so this rides a tested quoter, ideally
   `CommandMcpServer`'s.)*

**Runtime contract (S14):**
- **cwd = the workspace** — what the skill operates on and writes to.
- **`%~dp0` / `GXPT_SKILL_DIR` = the skill's own folder** — read-only asset source,
  located install-path-independently (`%~dp0` derives from the absolute path the host
  invokes, not from cwd, so the two never collide). Plus `GXPT_WORKDIR`,
  `GXPT_SKILL_SLUG`.
- Returns exit code + stdout + stderr (capped like other tool results).
- **Requires a workspace** (like `command__run`); no workspace ⇒ the tool isn't
  resolvable (workspace-less self-contained skills → temp scratch cwd, deferred).

---

## 6. Slash commands (`SlashCommandRouter`)

GxPT has no command system today (chat input is free-form), so this introduces a
**small client-side router** in the input path (`InputManager`). It parses the
leading `/token` and either handles it locally (no LLM turn) or transforms the turn.
Scoped to skills for now, but built as a general dispatcher.

| Command | Effect | Turn? |
|---------|--------|-------|
| `/<slug> [text]` | **Invoke**: pre-open `<slug>` for this turn (host injects its body as if `open_skill` were called), then send `text` as the user message | yes (LLM turn) |
| `/skills` | List available skills with their **effective** on/off state and where each came from (global default vs. this conversation), as a local transcript note | no |
| `/skills [on\|off] [here\|global]` | Toggle the **whole feature** (manifest + `open_skill`) at that scope; scope defaults to `here` (this conversation), `global` sets the app-wide default | no |
| `/skill <slug> [on\|off] [here\|global]` | Toggle **one skill** at the given scope; defaults to `here` | no |
| `/skill <slug> reset` · `/skills reset` | Drop the conversation override(s) so the skill / whole feature falls back to the global default | no |

- **Slug-first**, mirroring the `/tool <name> [on|off]` convention: the handle sits
  in the same position, and the verb (`on|off|reset`) is the optional second token.
  A bare `/skill <slug>` (verb omitted) **toggles** that skill, matching `/tool`'s
  no-verb behavior.
- **Scope keyword is the last token**, one of `here` (this conversation, the
  default) or `global` (the app-wide default in `skills.json`). Omitted ⇒ `here`.
- **Reserved slugs:** `skill` and `skills` (so the management verbs never collide
  with an invocation). The subcommand keywords (`on`/`off`/`reset`/`here`/`global`)
  only ever follow `/skill <slug>` or `/skills`, so they don't collide with
  `/<slug>` invocation. Slugs are kebab-case, so other collisions can't occur.
- **Invocation is sugar over the load path:** `/release-notes draft v2` →
  pre-open `release-notes` + user text `draft v2`. The host treats a pre-opened
  skill exactly like an `open_skill` result already in context — one load path (S3).
- **Management commands don't enter LLM history.** `/skills`, `/skill on|off`,
  `/skills on|off` produce a **local** transcript note and mutate conversation
  state; they are not sent to the model and not replayed. Only an **invocation**'s
  residual text becomes a user message.

---

## 7. Enablement & persistence

Two layers, resolved per request. Everything defaults to **all-on**, so absence at
both layers means a skill is enabled — newly-added bundled or project skills appear
automatically.

### Layer 1 — global default (`%AppData%/GxPT/skills.json`)
The app-wide baseline, written only by `… global` slash commands (no settings-form
surface, S6). A dedicated file like `recent-workdirs.json` / `mcp.json`:
```json
{ "feature_off": false, "disabled": ["noisy-skill"] }
```
- `feature_off` — whole-feature off everywhere unless a conversation overrides it.
- `disabled` — slugs off by default everywhere unless a conversation overrides them.

### Layer 2 — conversation override (conversation JSON)
Stored beside `WorkingDir` / `Zdr`, this layer **overrides** the global default for
one conversation and is **tri-state per skill** (a set can't force a skill *on* over
a global *off*, S10):
```json
{ "skills_feature_off": null, "skill_overrides": { "release-notes": true, "noisy-skill": false } }
```
- `skills_feature_off` — `null` inherits global; `true`/`false` forces the feature
  off/on for this conversation.
- `skill_overrides` — `slug → bool` (`true` force-on, `false` force-off); a slug
  absent here **inherits** the global default. `/skill reset` removes an entry;
  `/skills reset` clears the map and `skills_feature_off`.

### Resolution (each request)
1. **Feature:** `skills_feature_off` if non-null, else global `feature_off`. If off ⇒
   no manifest, no `open_skill` exposed, no trace in context (the memory M6 property,
   here per conversation).
2. **Per skill** (feature on): conversation `skill_overrides[slug]` if present, else
   *enabled unless* `slug ∈ global.disabled`.

Per-tab by construction — concurrent tabs carry their own override layer over the
shared global default. **No `settings.json` key, no checkbox**; all control is the
slash commands in §6.

---

## 8. Security & approval

- **A loaded `SKILL.md` body is injected instructions.** Bundled (first-party) and
  project-local skills are effectively author-trusted (a project skill carries the
  same trust as the repo the user opened). Treat skill content as untrusted input at
  the parse boundary regardless — the posture the architecture takes toward tool
  output (mcp-architecture §9) — and when user-global / shared skills land, gate a
  first open with the approval prompt so the user sees what's being loaded.
- **Execution is a *narrowed* surface, not `command__run` (S9).** `run_skill_script`
  lets the model run only the **declared entry of a named skill** with **literal
  args** — it cannot compose an arbitrary command or name a path. It is **Destructive
  (always-confirm)** but **remember-eligible by exact `(slug, relpath)`** (S15): a
  remembered allow is narrow and auditable, so it's *less* friction than `command__run`
  while *more* locked down. The prompt is human-readable ("skill `release-notes` wants
  to run `scripts/gen.bat --since v1.2` — *bundled*") and distinguishes bundled vs.
  project source.
- **Skills never operate on their own dir (S14).** cwd is the workspace; the skill
  folder is reached read-only for assets. Bundled skills typically sit in
  non-writable Program Files (an OS-level backstop), but the *write-only-to-workspace*
  rule is the real contract, since project skills live in the writable workspace tree.
- **No path or interpreter handling by the model.** `(slug, relpath)` handles plus the
  batch-only `%~dp0` wrapper convention (S12/S13) keep absolute paths and interpreter
  selection entirely host/author-side — the model never pastes a Program Files path
  into a shell.

---

## 9. Testing strategy

Same dual-world pattern as the rest of the repo (net48 linked-source via
`dotnet test`):

- **`SkillCatalog` + frontmatter parser** — discovery, project-over-bundled
  shadowing, malformed frontmatter, manifest assembly for a given enabled set.
- **`SlashCommandRouter`** — parsing, reserved-slug handling, scope keyword
  (`here`/`global`, default `here`), invocation transform (pre-open + residual
  text), management commands producing no LLM turn.
- **Enablement resolution** — the two-layer precedence: conversation override beats
  global default; **force-on over a global-off**; `reset` falls back to inherit;
  newly-added slugs default on at both layers.
- **Host loop (`GxPT.Tests`)** — `open_skill` resolution + body-as-tool-result (body +
  resolved skill dir + asset listing); manifest injected in the correct stable→volatile
  slot and reflecting the resolved enabled set; feature-off (either layer) ⇒ no
  manifest and no `open_skill` exposed; pre-opened skill equals a model `open_skill`.
- **`read_skill_file`** — relative-handle resolution within the skill root; rejects
  absolute / `..`-escape / drive paths; works for bundled and project skills.
- **`SkillsMcpServer` / `run_skill_script`** — slug+relpath resolution and sandbox
  rejection; `.bat`/`.cmd` extension allowlist; literal-arg quoting (no shell-metachar
  injection); cwd = workspace while `%~dp0` / `GXPT_SKILL_DIR` point at the skill root;
  unknown/disabled slug rejected; no-workspace ⇒ unresolvable.

---

## 10. Phasing / roadmap

1. **`SkillCatalog` + frontmatter parser** + bundled-skill discovery (pure, TDD).
2. **Manifest injection** in the orchestrator's ephemeral stack, gated by the
   per-conversation enabled set.
3. **`open_skill` meta-tool** (sibling to `reveal_tools`): body + resolved skill dir +
   asset listing as the tool result.
4. **`read_skill_file` meta-tool** (ReadOnly) for Level-3 assets.
5. **`SlashCommandRouter`** in `InputManager`: invocation + `/skills` listing +
   on/off toggles; per-conversation + global persistence.
6. **`SkillsMcpServer` + `run_skill_script`**: batch-only entry, handle resolution +
   sandbox, literal args, cwd = workspace, always-confirm (remember-eligible by
   `(slug, relpath)`). Added to `GxPT.sln`, the `AfterBuild` copy, and the setup
   `.vdproj` (like `MemoryMcpServer`).
7. **Project skills** (`<workdir>/.gxpt/skills/`) + project-over-bundled shadowing;
   ship a couple of bundled first-party skills (with a `%~dp0` wrapper) to dogfood.
8. **(Optional/later)** user-global skills; LRU cap on opened bodies; a
   model-authored-skills writer surface (mirrors `MemoryMcpServer`'s writer role).

---

## 11. Open / soft decisions

**Resolved (2026-06-08):**
- ~~`open_skill` host meta-tool vs. `SkillsMcpServer`~~ → catalog & reads are host
  meta-tools; execution is a `SkillsMcpServer` (S1/S11).
- ~~Scope~~ → bundled + project at launch, user-global deferred (S2).
- ~~Trigger model~~ → model-initiated (`open_skill`) **and** slash command, one
  catalog/load path (S3).
- ~~Per-skill settings UI~~ → dropped; enablement is slash-command driven, default
  all-on (S6).
- ~~Whether enablement should be promotable to an app-wide default~~ → yes: two
  scopes, `global` (a dedicated `skills.json`) and `here` (the conversation), with
  the conversation overriding the global default (S6/S10, §6/§7).
- ~~How the model invokes bundled scripts~~ → `run_skill_script(slug, relpath,
  args[])` in a sandboxed `SkillsMcpServer`, batch-only entry, handle-based (no
  absolute paths), cwd = workspace (S9/S11–S15, §5/§8).
- ~~Reading bundled assets `files__*` can't reach~~ → `read_skill_file(slug, relpath)`,
  ReadOnly, host-resolved within the skill root (S13).
- ~~Per-machine install path in authored markdown~~ → never author absolute paths;
  `open_skill` returns the resolved skill dir + asset listing, and `%~dp0` locates
  bundled siblings at runtime (S12, §5).

**Deferred:**
- User-global skills home and precedence (`project > user > bundled`).
- LRU cap on opened bodies (only if bodies crowd context).
- Workspace-less skill execution via a temp scratch cwd (a workspace is required
  today, S14).
- Non-batch direct entry types (`.exe`/`.py`/`.ps1`); today they're reached only
  through a `.bat`/`.cmd` wrapper (S12).
- A model-authored-skills writer surface (mirrors `MemoryMcpServer`'s writer role).
</content>
</invoke>
