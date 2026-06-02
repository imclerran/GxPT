# Chat Transcript — Rendering Performance

**Status:** Partially implemented (lazy tab rebuild done; the rest are open).
**Last updated:** 2026-06-02

This note documents a startup/rendering performance problem in the custom chat
transcript widget (`ChatTranscriptControl`) and the work to address it. One fix
(lazy per-tab rebuild) is implemented; the remaining items are recorded here as
deliberately deferred follow-ups.

---

## 1. Symptom

When the app reopens a previous session with **many tabs**, startup is slow: the
UI appears to hang and then all transcripts render at once. Closing most tabs
before exit makes the next launch much faster. The slowdown scales with the
**total content across all tabs**, even though only one tab is visible at a time
and only a small slice of that tab's transcript is on screen.

## 2. Root causes

### 2.1 Eager rebuild of every restored tab  *(fixed — see §3)*

`RestoreOpenTabsOnStartup` reopens every saved tab, and each open historically
called `RebuildTranscriptAsync` immediately. So all N transcripts were fully
parsed and laid out up front, regardless of visibility. Parsing runs off the UI
thread (a `ThreadPool` producer feeding a UI-timer consumer), but **layout runs
on the UI thread**, so N tabs means N producers and N UI timers all competing for
the UI thread — hence the "frozen, then everything appears" behavior.

### 2.2 Measurement is not virtualized  *(open)*

Painting *is* virtualized: `ChatTranscriptControl.OnPaint` skips items outside
the viewport. **Measurement is not.** `Reflow()` iterates every `MessageItem` and
calls `MeasureBubble` → `MeasureBlock` for every block to compute `_contentHeight`
(needed for the scrollbar range). So even a single long transcript pays
`O(all blocks)` on its first layout, and any later full reflow (resize, tab
activation) re-measures everything.

There is an append-only fast path (`ReflowAppendOnly`) that avoids re-measuring
earlier items during incremental batch consumption, so building a transcript is
not quadratic — but the first full measure still touches every block.

### 2.3 Per-code-block measurement is expensive  *(open)*

`MeasureBlock` for a `CodeBlock` creates a `Graphics`, enqueues syntax
highlighting, and calls `MeasureColoredSegmentsNoWrap`. This is the priciest path
in layout, and it runs for every code block in the transcript during measurement
— including code far off screen.

### 2.4 MCP servers launched per folder during restore  *(fixed — see §3)*

With per-working-directory MCP servers, each restored tab's `ApplyLoadedWorkingDir`
called `SyncMcpWorkingDirFromActiveTab`, which queued `EnsureWorkingDir` for that
tab's folder. So reopening a session launched a **files/git/command set per
distinct folder** (3 processes each) up front. Those spawns/handshakes — though
off the UI thread — saturate the `ThreadPool`, and the visible tab's
transcript-parse work item queues *behind* them, so the transcript appears late.
(The earlier single-active-workdir model only ever kept one set, so this was a
regression introduced with per-workdir servers.) MCP servers aren't needed until
a message is actually sent.

---

## 3. Implemented: lazy per-tab rebuild

At session restore, only the tab left **visible** has its transcript built; every
other restored tab is marked and built the first time it is selected. This makes
"many tabs" launch about as fast as "one tab" and directly matches the observed
behavior (closing tabs before exit reduces eager work).

Mechanism:

- `ChatTabContext.NeedsTranscriptRebuild` — set when a tab is opened but its
  transcript is deferred.
- `MainForm._restoringTabs` — true only during the restore loop. `OpenConversation*`
  checks it: while restoring, it sets `NeedsTranscriptRebuild` instead of calling
  `RebuildTranscriptAsync`.
- After the restore loop, `HydrateTabIfNeeded` builds the active tab.
- `OnTabSelected` calls `HydrateTabIfNeeded` for the selected tab (skipped while
  `_restoringTabs` is true), so deferred tabs build on first view.

This change is confined to the restore / tab-switch path in `MainForm` and the
flag on `ChatTabContext`; it does **not** touch the widget's measure/paint
internals. A hydrated transcript persists for the session (tab switches don't
tear it down), so a tab is built at most once.

**MCP launch deferral (§2.4).** `SyncMcpWorkingDirFromActiveTab` is a no-op while
`_restoringTabs` is true, so no servers spawn during restore. After restore it is
called once to pre-warm the visible tab's folder; every other tab's servers
launch lazily on first send (`BeginToolSend` already calls `EnsureWorkingDir`) or
when the tab is first selected. This keeps the thread pool free for the visible
tab's transcript parse.

Non-goals of this change: it does not speed up a *single* very long transcript,
and it doesn't reduce work when the user eventually visits every tab.

---

## 4. Deferred follow-ups

These were intentionally left for later; they address the remaining cost,
especially for a single long transcript.

### 4.1 Measure-time virtualization  *(biggest, structural)*

Avoid measuring off-screen items exactly. Options:
- Cache each item's last-measured height and reuse it until its width or content
  changes, so a full reflow re-measures only dirty items.
- Estimate off-screen heights (e.g. from line/char counts) for the scrollbar
  range and perform the exact, expensive measure lazily as items scroll into
  view — true virtualization rather than paint-only.

This is the most impactful for long transcripts but the most invasive: it touches
`Reflow`, `_contentHeight`, scrollbar math, and hit-testing, which all currently
assume every item has exact bounds.

### 4.2 Defer syntax-highlight measurement  *(medium)*

Give code blocks a cheap height estimate (line count × line height + chrome)
during layout and only measure colored segments when the block is actually
painted/visible. Cuts the worst per-block cost (§2.3) without full virtualization.

### 4.3 Idle-scheduled hydration  *(small, optional)*

If pre-building all tabs is desirable (so later switches are instant), hydrate
deferred tabs one at a time on `Application.Idle` after the active tab is ready,
rather than all at once — keeping the UI responsive while warming the rest.

---

## 5. Key code references

- `MainForm.RestoreOpenTabsOnStartup`, `HydrateTabIfNeeded`, `OnTabSelected`,
  `OpenConversationInTab` / `OpenConversationInNewTab` — restore + lazy hydrate.
- `TabManager.ChatTabContext.NeedsTranscriptRebuild` — deferral flag.
- `ChatTranscriptControl.Reflow` / `ReflowAppendOnly` / `MeasureBubble` /
  `MeasureBlock` — the (non-virtualized) measurement path.
- `ChatTranscriptControl.OnPaint` — the virtualized paint path (for contrast).
