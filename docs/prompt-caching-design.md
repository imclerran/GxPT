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
- **Breakpoints** are `ChatMessage.CacheControl` flags, placed by
  `McpChatOrchestrator.ApplyCacheBreakpoints`. Zone A's flag goes on a request-local head
  message; Zone B's goes on a `WithCacheControl()` clone so the flag never lands in persisted
  history (stale flags would accumulate past Anthropic's 4-breakpoint limit). The two spare
  slots become **intermediate flags spaced ~12 estimated content blocks apart** walking back
  from the newest message: Anthropic's matcher only looks back ~20 blocks from a breakpoint for
  a prior cache entry, so a single iteration appending more than that (an assistant message
  with K tool calls renders ~K+1 blocks plus K result blocks; K >= ~10) would otherwise leave
  the next request unable to find the previous entry - a silent full miss. Bridged up to ~18
  calls per iteration; a single assistant message wider than the lookback (~20+ calls) is
  unbridgeable by placement and accepted.
- During the tool-call loop, breakpoint #2 rides the newest tool result, so iteration N+1 reads
  everything through iteration N from cache. This is where most of the savings are.

## 3. Wire format (OpenRouterClient)

- A flagged message's content is emitted as `[{type:"text", text, cache_control:{type:"ephemeral"}}]`
  — for **every** model, not just explicit-caching vendors. Providers without explicit caching
  ignore the annotation (documented by OpenRouter; field-verified harmless), and the markers can
  matter even for models whose author caches automatically: a third-party host may implement
  explicit-marker caching only (suspected of SiliconFlow-hosted DeepSeek, which never auto-cached
  prefix-stable requests). The original vendor gate (`ModelSupportsCacheControl`) was removed
  once the ignored-not-errored behavior was field-verified.
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

## 5. Sticky provider routing

A model on OpenRouter can be served by several provider endpoints (Anthropic, Amazon Bedrock,
Google Vertex, ...), and **prompt caches live per provider**: an entry written by Anthropic is
invisible to Bedrock. Routing that flaps between endpoints fragments the cache — partial hits at
best (bounded by the TTL running while a provider sits unused, and by the ~20-block matcher
lookback), double write-premiums at worst.

So requests on cache-supported models carry `provider.order = [<cache-warm provider>]`, where
"cache-warm" means **confirmed by a demonstrated hit**, not merely "served last":

- OpenRouter reports the serving provider on response chunks (`chunk.provider`) and the usage
  accounting (cache counters, cost estimate) on the final usage chunk; the client surfaces it
  once per request as a `ResponseUsage` via `ClientProperties.ResponseUsageCallback`. The same
  callback feeds the status bar's per-conversation cost/savings accounting
  (`Conversation.RecordUsage`), which runs on every model - only the stickiness gate inside it is
  caching-model-gated.
- **`cache_discount` does not ride the SSE stream** - the post-hoc generation record
  (`GET /api/v1/generation?id=` -> `total_cost`, `cache_discount`, `provider_name`;
  `OpenRouterClient.FetchGenerationStats` + `Conversation.ReconcileUsage`) is the Saved
  figure's only data source. The streamed `usage.cost` was observed billing-accurate (cache
  pricing included) on Bedrock-served Anthropic models, but that isn't guaranteed across
  providers, so caching-capable models reconcile every request; the delta-based reconcile is a
  no-op when the stream was already accurate. Operational notes: the record can take several
  seconds to become queryable (the fetch retries patiently and logs failures - a silent fetch
  failure looks like "Saved frozen at $0.00"); the reconcile gate must not depend on streamed
  cache counters being present; and a nonzero billed `cache_discount` also latches
  `CacheWarmProvider` as a late cache-activity signal for streams that omit counters.
- Semantics note: OpenRouter normalizes usage to OpenAI conventions - `prompt_tokens` is the
  TOTAL prompt size and the cache counters are subsets of it (unlike Anthropic's native API,
  where `input_tokens` is the uncached remainder). Never add the counters to `prompt_tokens`.
- Stickiness is **confirmation-gated**: only a response demonstrating cache activity - a read
  (`cached_tokens > 0`) or a write (`cache_write_tokens > 0`) - sets or moves the preference.
  Merely serving proves nothing (a third-party host of an open-weights model may not cache at
  all, and pinning there would constrain load balancing for no benefit); a read proves the warm
  cache lives here, a write proves it was just created here. Explicit-caching providers
  therefore latch from the conversation's first request; implicit cachers whose writes aren't
  reported latch on their first observed hit.
- A later no-activity response from the confirmed provider does NOT clear the preference: that
  is usually TTL expiry between turns, where keeping the rebuild on one provider is exactly the
  point. The preference moves only when a different provider demonstrates cache activity.
- The orchestrator keeps the value in `PreferredProvider` (mid-turn loop iterations follow the
  warm cache immediately); the host persists it as `Conversation.CacheWarmProvider`.
- `order` is a preference with fallback (allow_fallbacks defaults true), not a pin: an outage
  reroutes, and once the substitute provider produces hits the preference follows it. This
  self-corrects in a way a static "first-party provider" map would not.
- Gated by `ModelSupportsPromptCaching`: non-caching models keep full load balancing.

## 6. Invalidation events (what still misses, by design)

| Event | Cost | Frequency |
|---|---|---|
| reveal_tools / gate flip / cap eviction | one full prefix re-write | occasional, implies new work anyway |
| workspace or model change mid-conversation | one full re-write / cold cache | rare |
| memory write, skills change, manifest change | nothing (Zone C) | — |
| loop iteration, normal user turn | incremental read + small write | the common case |
| > provider TTL (~5 min) between turns | one re-write | normal chat pacing |

## 7. Rules for future changes

1. Never interpolate timestamps, IDs, counters, or any per-request value into Zone A or the
   manifests' position before history. Volatile content goes in Zone C.
2. Never reorder or conditionally serialize the tools array; keep emission name-sorted and
   deterministic.
3. `RequestMessageTransform` implementations must be byte-deterministic per message.
4. Never set `CacheControl` on a persisted `ChatMessage`; flag request-local objects or clones.
5. After touching request assembly, verify with the `Usage` log line: `cached` should be a large
   share of `prompt` from the second request of a conversation onward (on caching providers).
