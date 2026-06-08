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
- **Host-native, no new server on the read path.** Discovery + the load meta-tool
  live in the GxPT host, exactly like `reveal_tools` (a host-synthesized meta-tool,
  never routed to a connection — D11 in `mcp-architecture.md`).
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
| S1 | **Host-synthesized `open_skill(names[])` meta-tool**, not a `SkillsMcpServer` | Disk is the source of truth and the catalog is read-only, so a stdio process buys nothing on the read path. `open_skill` is a sibling of `reveal_tools` — in the exposed `tools` array but handled inside the host registry, never routed to a connection (D11). A server only earns its place later if the *model* must author skills (then it mirrors `MemoryMcpServer`). |
| S2 | **Bundled + project scope at launch** (`<exe>/skills/` and `<workdir>/.gxpt/skills/`); user-global deferred | Bundled "starter" skills are the main draw, so unlike memory (project-only by choice) skills lead with bundled. Project skills reuse the `.gxpt/` convention the memory system established. |
| S3 | **Model-initiated *and* slash-command triggers**, over one catalog | `open_skill` matches GxPT's `reveal_tools` precedent (the model picks from the manifest mid-task); the slash command gives the user a deliberate "run this now" entry point. Both pre-/open the same `SKILL.md`, so there is one load path, not two. |
| S4 | **`SKILL.md` = minimal frontmatter + markdown body**, parsed by a hand-rolled reader | net35 has no YAML parser and the repo keeps exactly one JSON lib (Newtonsoft, D3). A ~20-line `--- … ---` reader for `name`/`description` keeps authoring familiar to Claude Code skills without a new dependency or a JSON sidecar. |
| S5 | **Folder name = slug = handle**; reuse the memory system's kebab-case `Slug` | No name→path map needed (memory M4). `<workdir>/.gxpt/skills/<slug>/SKILL.md`; the slug is what appears in the manifest, what `open_skill` takes, and what the slash command types. |
| S6 | **No skills UI in `SettingsForm`; enablement is per-conversation via slash commands**, default **all-on** | Skills are a discoverable capability, not a privacy-sensitive opt-in like memory — so they default on and need no settings toggle. Per-conversation scope matches `Zdr`/`WorkingDir` grain (different tabs, different skill sets). Stored as a **disabled-set** (+ a global off flag) so newly-added bundled skills auto-appear. |
| S7 | **`open_skill` returns the body as the tool result** (not a separate ephemeral injection) | Returning through the normal tool-loop lands the body in history naturally and keeps it available for the rest of the task — no new injection channel. The always-on **manifest** still rides as an ephemeral system message (S8). |
| S8 | **Manifest injected as an ephemeral system message**, in the stable→volatile stack | Same treatment as the names manifest and the memory block: rebuilt each request from the live catalog, never persisted. Cheap (names + one-liners), so it sits in front of history every turn when skills are enabled for the conversation. |
| S9 | **Bundled scripts run through the existing approval tiers** — skills get no new execution channel | A skill body instructs the model to use `files__*` / `command__run`, which already hit the three-tier gate (Destructive = always-confirm). A skill that runs code is exactly as gated as the command tool, no more (mcp-architecture §9). |

---

## 3. On-disk layout

### Bundled (first-party, ships beside the exe)
```
<GxPT.exe dir>/skills/
  <slug>/
    SKILL.md           # frontmatter + body
    <bundled files…>   # templates, examples, scripts (optional)
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

---

## 4. Disclosure levels

| Level | Content | When it enters context | Cost |
|-------|---------|------------------------|------|
| 1 | `slug` + `description` (the **manifest**) | every request, while skills are enabled for the conversation (S8) | tiny |
| 2 | the **`SKILL.md` body** | when `open_skill(slug)` is called (by model or slash) (S7) | medium |
| 3 | **bundled files/scripts** | read via `files__*`, run via `command__run`, as the body directs (S9) | paid on use |

---

## 5. Host pieces (`Services/Skills/`)

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
  └─ exposed tools: reveal_tools, open_skill(names[]), …revealed tools
                                   └─ host-handled (S1): reads SKILL.md from disk,
                                      returns body + bundled-file listing as the tool result (S7)
```

- **`SkillCatalog`** — scans bundled + project skill folders, parses each frontmatter
  into `(slug, description, path)`, applies project-over-bundled shadowing, and
  produces `SkillsManifestSystemMessage(enabledSet)` and `slug → path` resolution.
  Pure logic (no WinForms) → net48 linked-source tests.
- **`open_skill(names[])`** — host-synthesized meta-tool, registered alongside
  `reveal_tools` in the registry. Reads the named `SKILL.md` bodies, returns each
  body plus a listing of its bundled files as the tool result. Batch (plural) for
  the same reason `reveal_tools` is batch: resolve ambiguity by opening candidates.
- **Optional LRU cap** on how many full bodies persist, reusing the `reveal_tools`
  reveal-cap machinery — only if skill bodies start to crowd context. Not needed
  initially.

---

## 6. Slash commands (`SlashCommandRouter`)

GxPT has no command system today (chat input is free-form), so this introduces a
**small client-side router** in the input path (`InputManager`). It parses the
leading `/token` and either handles it locally (no LLM turn) or transforms the turn.
Scoped to skills for now, but built as a general dispatcher.

| Command | Effect | Turn? |
|---------|--------|-------|
| `/<slug> [text]` | **Invoke**: pre-open `<slug>` for this turn (host injects its body as if `open_skill` were called), then send `text` as the user message | yes (LLM turn) |
| `/skills` | List available skills with their on/off state, rendered as a local transcript note | no |
| `/skills on` · `/skills off` | Global per-conversation toggle (whole manifest + `open_skill` on/off) | no |
| `/skill on <slug>` · `/skill off <slug>` | Per-skill toggle for this conversation | no |

- **Reserved slugs:** `skill` and `skills` (so the management verbs never collide
  with an invocation). Slugs are kebab-case, so other collisions can't occur.
- **Invocation is sugar over the load path:** `/release-notes draft v2` →
  pre-open `release-notes` + user text `draft v2`. The host treats a pre-opened
  skill exactly like an `open_skill` result already in context — one load path (S3).
- **Management commands don't enter LLM history.** `/skills`, `/skill on|off`,
  `/skills on|off` produce a **local** transcript note and mutate conversation
  state; they are not sent to the model and not replayed. Only an **invocation**'s
  residual text becomes a user message.

---

## 7. Enablement & persistence

- **Per-conversation, default all-on** (S6). The enabled set is derived as
  *all discovered skills minus a disabled-set*, so newly-added bundled or project
  skills appear automatically.
- **Stored with the conversation** (alongside `WorkingDir` / `Zdr` in the
  conversation JSON): a `DisabledSkills` slug list + a `SkillsOff` global bool.
  Compact, forward-compatible, and per-tab — concurrent tabs can carry different
  skill sets, matching the workdir-scoped server model.
- **When a conversation has skills globally off** (`/skills off`): no manifest, no
  `open_skill` exposed, no trace in context — the per-conversation analogue of the
  memory M6 "disabled leaves no trace" property.
- **No `SettingsForm` surface.** No `settings.json` key, no checkbox. All control is
  the slash commands above.

---

## 8. Security & approval

- **A loaded `SKILL.md` body is injected instructions.** Bundled (first-party) and
  project-local skills are effectively author-trusted. Treat skill content as
  untrusted input at the parse boundary regardless — the same posture the
  architecture takes toward tool output (mcp-architecture §9) — and the day
  user-global / shared skills land, gate a first open with the approval prompt so
  the user sees what's being loaded.
- **No new execution channel (S9).** Bundled scripts run only because the body tells
  the model to invoke `command__run` / `files__*`, which already pass through the
  three-tier gate (Destructive = always-confirm, argument-scoped allowlists). A
  skill is never a way to bypass approval.

---

## 9. Testing strategy

Same dual-world pattern as the rest of the repo (net48 linked-source via
`dotnet test`):

- **`SkillCatalog` + frontmatter parser** — discovery, project-over-bundled
  shadowing, malformed frontmatter, manifest assembly for a given enabled set.
- **`SlashCommandRouter`** — parsing, reserved-slug handling, invocation transform
  (pre-open + residual text), management commands producing no LLM turn.
- **Host loop (`GxPT.Tests`)** — `open_skill` resolution + body-as-tool-result;
  manifest injected in the correct stable→volatile slot; `/skills off` ⇒ no
  manifest and no `open_skill` exposed; pre-opened skill equals a model `open_skill`.

---

## 10. Phasing / roadmap

1. **`SkillCatalog` + frontmatter parser** + bundled-skill discovery (pure, TDD).
2. **Manifest injection** in the orchestrator's ephemeral stack, gated by the
   per-conversation enabled set.
3. **`open_skill` meta-tool** in the registry (sibling to `reveal_tools`), body as
   tool result.
4. **`SlashCommandRouter`** in `InputManager`: invocation + `/skills` listing +
   on/off toggles; per-conversation persistence in the conversation JSON.
5. **Project skills** (`<workdir>/.gxpt/skills/`) + project-over-bundled shadowing;
   ship a couple of bundled first-party skills to dogfood.
6. **(Optional/later)** user-global skills; LRU cap on opened bodies; a
   `SkillsMcpServer` only if the model should author skills at runtime.

---

## 11. Open / soft decisions

**Resolved (2026-06-08):**
- ~~`open_skill` host meta-tool vs. `SkillsMcpServer`~~ → host meta-tool (S1).
- ~~Scope~~ → bundled + project at launch, user-global deferred (S2).
- ~~Trigger model~~ → model-initiated (`open_skill`) **and** slash command, one
  catalog/load path (S3).
- ~~Per-skill settings UI~~ → dropped; enablement is per-conversation via slash
  commands, default all-on (S6).

**Deferred:**
- User-global skills home and precedence (`project > user > bundled`).
- LRU cap on opened bodies (only if bodies crowd context).
- A `SkillsMcpServer` writer role for model-authored skills.
- Whether `/skills` state should ever be promotable to an app-wide default.
</content>
</invoke>
