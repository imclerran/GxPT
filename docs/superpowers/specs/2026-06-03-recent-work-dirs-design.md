# Design: "Open Recent Work Dir" File-menu feature

**Date:** 2026-06-03
**Status:** Approved (design)

## Summary

Remember the last 5 working directories the user has opened in GxPT and expose them
through a new **Open Recent Work Dir** submenu in the File menu. Selecting an entry
opens a brand-new conversation tab with that directory preset as the conversation's
working folder.

## Decisions

- **Capture point:** A directory is recorded as "recent" both when the user explicitly
  picks a folder *and* whenever a loaded conversation's working dir becomes active.
- **Stale dirs:** Directories that no longer exist on disk are silently dropped when the
  submenu is built. Stored entries are left untouched (a dir that reappears can show up
  again).
- **Label:** Each entry shows the **full path**.
- **Storage location:** A dedicated JSON file, **not** `settings.json`.
- **Cap:** 5 entries, most-recent-first.

## Components

### 1. `RecentWorkDirs` store — new file `GxPT/Services/RecentWorkDirs.cs`

A static class that owns the recent-dirs list and its persistence. XP / .NET 3.5
compatible: uses `System.Web.Script.Serialization.JavaScriptSerializer` (matching
`AppSettings`) and `FileSafe.WriteAllTextAtomic` for safe writes.

- **File path:** `%APPDATA%\GxPT\recent-workdirs.json`, built from
  `AppSettings.SettingsDirectory`. The file holds a JSON array of strings, ordered
  most-recent-first, e.g. `["C:\\a","C:\\b"]`.
- **Testability:** an `internal static string FilePathOverride` lets tests redirect IO
  to a temp file. When null, the default `%APPDATA%` path is used.
- **Constant:** `MaxEntries = 5`.

Public surface:

- `static List<string> Get()` — reads the file and returns the stored list
  (most-recent-first). Returns an empty list on any error or missing file. Performs **no**
  existence filtering — that is the menu builder's job.
- `static void Add(string path)` — records `path` as the most recent:
  1. Ignore null/empty/whitespace.
  2. Normalize: `Path.GetFullPath` (best effort) and trim a trailing
     directory separator so `C:\a` and `C:\a\` collapse.
  3. Remove any existing case-insensitive duplicate (`StringComparer.OrdinalIgnoreCase`,
     appropriate for Windows paths).
  4. Insert at index 0.
  5. Trim to `MaxEntries`.
  6. Write atomically.

  Read-modify-write is wrapped in a simple `lock` for parity with `AppSettings`'
  thread-safety posture (the naming/UI threads can both touch working dirs).

The core list transform (dedup + move-to-front + cap) is simple enough to be covered by
exercising `Add`/`Get` round-trips against a temp file; no separate pure helper is
required.

### 2. Capture wiring — `GxPT/Forms/MainForm.cs`

Call `RecentWorkDirs.Add(dir)` at the points where a working dir becomes active:

- `SetWorkingFolderForContext` (~line 713) — after `ctx.WorkingDir = dlg.SelectedPath;`,
  add the chosen folder.
- `ApplyLoadedWorkingDir` (~line 737) — after adopting the conversation's working dir,
  add it when non-empty.
- `OpenNewTabWithWorkingDir` (new, below) — adds the dir so re-opening a recent moves it
  back to the top.

### 3. Menu item — `GxPT/Forms/MainForm.Designer.cs`

Add a `ToolStripMenuItem miOpenRecentWorkDir` ("Open Recent Work &Dir") to
`miFile.DropDownItems`, positioned immediately after `miNewConversation`. It is a parent
menu whose children are built dynamically.

### 4. Dynamic population — `GxPT/Forms/MainForm.cs`

Handle the File menu's `DropDownOpening` (wire `miFile.DropDownOpening` in the form ctor /
designer) to rebuild the submenu each time it is shown:

- Clear existing child items (disposing them).
- For each path from `RecentWorkDirs.Get()` where `Directory.Exists(path)` is true, add a
  child `ToolStripMenuItem` whose `Text` is the full path and whose `Click` calls
  `OpenNewTabWithWorkingDir(path)`. Capture `path` in a local to avoid the closure-capture
  pitfall.
- If no valid entries remain, set `miOpenRecentWorkDir.Enabled = false`; otherwise
  `Enabled = true`.

Rebuilding on every open keeps the list current and naturally drops stale dirs without a
separate refresh mechanism.

### 5. Opening helper — `GxPT/Forms/MainForm.cs`

```
private void OpenNewTabWithWorkingDir(string dir)
{
    if (_tabManager == null) return;
    var ctx = _tabManager.CreateConversationTab();
    if (ctx == null) return;
    ctx.WorkingDir = dir;
    if (ctx.Conversation != null) ctx.Conversation.WorkingDir = dir;
    PersistWorkingDir(ctx);
    ApplyLoadedWorkingDir(ctx);   // shows the workspace strip + binds MCP host
    RecentWorkDirs.Add(dir);
}
```

This mirrors the existing load flow (`CreateConversationTab` → set working dir →
`PersistWorkingDir` → `ApplyLoadedWorkingDir`), so the new tab behaves exactly like a
conversation opened with a saved working folder.

## Data flow

1. User sets / loads / opens-recent a working dir → `RecentWorkDirs.Add(dir)` →
   `recent-workdirs.json` updated.
2. User opens the File menu → `DropDownOpening` → `RecentWorkDirs.Get()` → filter by
   `Directory.Exists` → submenu rebuilt.
3. User clicks an entry → `OpenNewTabWithWorkingDir(path)` → new tab with working dir set,
   workspace strip shown, MCP bound, and the dir bumped to the top of the recents.

## Error handling

- All file IO in `RecentWorkDirs` is wrapped in try/catch; failures degrade to an empty
  list / no-op (consistent with `AppSettings` and `PersistWorkingDir`).
- Closure capture in the dynamic menu uses a per-item local to avoid every item opening
  the last directory.

## Testing

`GxPT.Tests/RecentWorkDirsTests.cs` (xUnit), using `FilePathOverride` pointed at a temp
file (cleaned up via `IDisposable`, matching `FileSafeTests`):

- `Add` then `Get` returns the path.
- Adding a duplicate (including a different-case / trailing-slash variant) does not create
  a second entry and moves it to the front.
- Most-recent-first ordering after several adds.
- The list is capped at 5; the oldest entry falls off.
- `Get` on a missing file returns an empty list.
- A persisted file round-trips across separate `Get` calls (serialization works).

Menu wiring and tab opening are verified manually (WinForms UI is not unit-tested in this
project).

## Out of scope (YAGNI)

- Pinning / favorites.
- A "clear recent" command.
- Configurable entry count.
- Per-directory metadata (last-used time, project name, etc.).
