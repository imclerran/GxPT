# Prompt Caching Design

Provider prompt caching (via OpenRouter) bills a request's repeated prompt prefix at a steep
discount instead of full price. This document records how GxPT structures its requests to hit
that cache, and the invariants every future change to request assembly must preserve.

> Supersedes the LRU/eviction model described in `mcp35-discovery-spec.md` §8/§13 and the
> "revealed set (LRU, registry-owned)" wording in `mcp35-toolloop-spec.md`: the revealed set now
> lives on the Conversation (append-only on caching providers; cap-trimmed at turn start on
> non-caching ones), and the registry is stateless with respect to reveals.

## 1. The one invariant

**Prompt caching is a prefix match.** The provider caches the rendered prompt up to a marker (or
automatically, depending on provider) and re-serves it when a later request starts with the exact
same bytes. One changed byte at position N re-bills everything after N. The tools array renders
at position 0, before system messages and history, so it is the most invalidation-sensitive
content in the request.

Consequence: everything in the request is ordered by volatility, and anything that can change
between requests must come after everything that cannot.

## 2. Request layout (zones)

Assembled per request in `McpChatOrchestrator.RunTurn` (tool turns) and `MainForm` (plain turns):

```
tools array            append-only per conversation, name-sorted        ┐ Zone A
emoji system message   constant (prepended by OpenRouterClient)         │ frozen for the
agent system prompt    constant                                         │ conversation
workspace system msg   constant while the workspace is unchanged        ┘
        ◄── cache breakpoint #1 (last system message)
persisted history      append-only (user/assistant/tool messages)        Zone B
        ◄── cache breakpoint #2 (newest persisted message, cloned)
ephemeral context tail rebuilt every request, never cached               Zone C
  [Ephemeral context...] <memory> <skills> <available_tools>
```

- **Zone C is one trailing user-role message** (`BuildEphemeralContextText`). User role because
  OpenRouter hoists in-array system messages to Anthropic's top-level system parameter, which
  would put the volatile content back in front of the cached history. It is never persisted and
  never rendered by the UI; the agent prompt tells the model what it is. Memory, the skills
  manifest, and the MCP names manifest may all change per request at zero cache cost here.
- **Breakpoints** are `ChatMessage.CacheControl` flags. Zone A's flag goes on a request-local
  head message; Zone B's goes on a `WithCacheControl()` clone so the flag never lands in
  persisted history (stale flags would accumulate past Anthropic's 4-breakpoint limit).
- During the tool-call loop, breakpoint #2 rides the newest tool result, so iteration N+1 reads
  everything through iteration N from cache. This is where most of the savings are.

## 3. Wire format (OpenRouterClient)

- A flagged message's content is emitted as `[{type:"text", text, cache_control:{type:"ephemeral"}}]`
  — but only when `ModelSupportsCacheControl(model)` (vendor prefixes `anthropic/`, `google/`,
  with `~` aliases stripped). Other providers cache automatically (OpenAI, DeepSeek, Grok) or not
  at all; they get plain string content.
- Every request carries `usage: {include: true}`. The final SSE chunk's
  `usage.prompt_tokens_details.cached_tokens` is logged (`Usage` log tag) — this is the
  verification signal. `cached=0` on a long conversation means a silent invalidator.
- The cap wrap-up request keeps the loop's tools array and sets `tool_choice: "none"` instead of
  dropping tools: removing the array would change position 0 and forfeit the tools+system cache.

## 4. Reveal system

- The revealed-tool set is **per-conversation** (`Conversation.RevealedTools`, persisted), not
  registry-global: concurrent tabs no longer churn each other's tools array, and a reopened
  conversation resumes with its working set instead of starting cold.
- The list is recency-ordered (reveal and call bump a name to the end) but **emitted name-sorted**
  (`McpToolRegistry.ExposedFunctionDefs`), so neither reveal order nor calling a tool reorders
  the serialized array. (The old recency-sorted emission reordered the array on every call —
  a guaranteed full cache miss every loop iteration.)
- **Eviction is provider-gated** (`McpChatOrchestrator`, turn start only):
  - Prompt-caching providers (`OpenRouterClient.ModelSupportsPromptCaching`): never evict.
    An evicted def changes the tools array and re-bills the whole transcript once, which always
    costs more than the few hundred cache-discounted tokens a stale def occupies.
  - Non-caching providers: trim to `RevealEvictionCap` (24), least recently used first — stale
    defs are pure per-turn cost there.
- Stale revealed names (server removed/disabled) are skipped at emission time, never pruned from
  the conversation's list, so a returning server restores its defs.

## 5. Invalidation events (what still misses, by design)

| Event | Cost | Frequency |
|---|---|---|
| reveal_tools / gate flip / cap eviction | one full prefix re-write | occasional, implies new work anyway |
| workspace or model change mid-conversation | one full re-write / cold cache | rare |
| memory write, skills change, manifest change | nothing (Zone C) | — |
| loop iteration, normal user turn | incremental read + small write | the common case |
| > provider TTL (~5 min) between turns | one re-write | normal chat pacing |

## 6. Rules for future changes

1. Never interpolate timestamps, IDs, counters, or any per-request value into Zone A or the
   manifests' position before history. Volatile content goes in Zone C.
2. Never reorder or conditionally serialize the tools array; keep emission name-sorted and
   deterministic.
3. `RequestMessageTransform` implementations must be byte-deterministic per message.
4. Never set `CacheControl` on a persisted `ChatMessage`; flag request-local objects or clones.
5. After touching request assembly, verify with the `Usage` log line: `cached` should be a large
   share of `prompt` from the second request of a conversation onward (on caching providers).
