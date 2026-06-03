# Open Recent Work Dir Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remember the last 5 working directories the user opened and add an "Open Recent Work Dir" File-menu submenu that opens each in a new conversation tab.

**Architecture:** A new static `RecentWorkDirs` store persists the list (most-recent-first, capped at 5) to a dedicated `%APPDATA%\GxPT\recent-workdirs.json` file. `MainForm` records a directory whenever one becomes active (explicit pick or conversation load), and a dynamically-populated File-menu submenu opens a fresh tab for any still-existing recent dir.

**Tech Stack:** C# 3.0 / .NET 3.5 (app assembly `GxPT`); WinForms; `JavaScriptSerializer` (System.Web.Extensions) for JSON; `FileSafe.WriteAllTextAtomic` for crash-safe writes. Tests are xUnit on `net48` via `dotnet test`, linking GxPT source files directly.

**Constraints:**
- `GxPT/Services/RecentWorkDirs.cs` compiles under **C# 3.0 / .NET 3.5** — no `var` issues (allowed), but no LINQ query niceties beyond what 3.5 supports, no string interpolation, no expression-bodied members.
- The app's `internal` members are visible to the test project only because it **links the source file**; the new file must be added to `GxPT.Tests.csproj`.

---

### Task 1: `RecentWorkDirs` store (TDD)

**Files:**
- Create: `GxPT/Services/RecentWorkDirs.cs`
- Modify: `GxPT.Tests/GxPT.Tests.csproj` (add a linked-source `<Compile Include>` after line 55)
- Test: `GxPT.Tests/RecentWorkDirsTests.cs`

- [ ] **Step 1: Create the store with the full implementation**

Create `GxPT/Services/RecentWorkDirs.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace GxPT
{
    // Remembers the most-recently-used working directories (most-recent-first, capped),
    // persisted to its own JSON file alongside settings.json. XP / .NET 3.5 compatible.
    internal static class RecentWorkDirs
    {
        public const int MaxEntries = 5;

        // Tests redirect IO here; null means use the default %APPDATA%\GxPT path.
        internal static string FilePathOverride;

        private static readonly object _gate = new object();

        private static string FilePath
        {
            get
            {
                if (!string.IsNullOrEmpty(FilePathOverride)) return FilePathOverride;
                return Path.Combine(AppSettings.SettingsDirectory, "recent-workdirs.json");
            }
        }

        // Returns the stored list, most-recent-first. No existence filtering here.
        public static List<string> Get()
        {
            lock (_gate)
            {
                return ReadLocked();
            }
        }

        // Records 'path' as the most-recent entry: dedup (case-insensitive), move to front, cap.
        public static void Add(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Trim().Length == 0) return;

            string norm = path.Trim();
            try { norm = Path.GetFullPath(norm); }
            catch { }
            norm = norm.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (norm.Length == 0) return;

            lock (_gate)
            {
                List<string> list = ReadLocked();
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(list[i], norm, StringComparison.OrdinalIgnoreCase))
                        list.RemoveAt(i);
                }
                list.Insert(0, norm);
                while (list.Count > MaxEntries) list.RemoveAt(list.Count - 1);
                WriteLocked(list);
            }
        }

        private static List<string> ReadLocked()
        {
            List<string> result = new List<string>();
            try
            {
                string path = FilePath;
                if (!File.Exists(path)) return result;
                string text = File.ReadAllText(path, Encoding.UTF8);
                if (string.IsNullOrEmpty(text)) return result;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                object[] arr = ser.DeserializeObject(text) as object[];
                if (arr != null)
                {
                    foreach (object item in arr)
                    {
                        if (item != null) result.Add(Convert.ToString(item));
                    }
                }
            }
            catch { }
            return result;
        }

        private static void WriteLocked(List<string> list)
        {
            try
            {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                string json = ser.Serialize(list);
                string path = FilePath;
                string dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                FileSafe.WriteAllTextAtomic(path, json, new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
```

- [ ] **Step 2: Add the store to the test project's linked sources**

In `GxPT.Tests/GxPT.Tests.csproj`, after the `AppSettings.cs` line (line 55):

```xml
    <Compile Include="..\GxPT\Services\AppSettings.cs" Link="Linked\AppSettings.cs" />
    <Compile Include="..\GxPT\Services\RecentWorkDirs.cs" Link="Linked\RecentWorkDirs.cs" />
```

(`FileSafe.cs` is already linked at line 35, and `System.Web.Extensions` is already referenced at line 77, so `JavaScriptSerializer` resolves.)

- [ ] **Step 3: Write the failing tests**

Create `GxPT.Tests/RecentWorkDirsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using GxPT;
using Xunit;

namespace GxPT.Tests
{
    public sealed class RecentWorkDirsTests : IDisposable
    {
        private readonly string _root;
        private readonly string _file;

        public RecentWorkDirsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gxpt_recentdirs_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _file = Path.Combine(_root, "recent-workdirs.json");
            RecentWorkDirs.FilePathOverride = _file;
        }

        public void Dispose()
        {
            RecentWorkDirs.FilePathOverride = null;
            try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
            catch { }
        }

        [Fact]
        public void Get_OnMissingFile_ReturnsEmpty()
        {
            List<string> list = RecentWorkDirs.Get();
            Assert.Empty(list);
        }

        [Fact]
        public void Add_ThenGet_ReturnsThePath()
        {
            RecentWorkDirs.Add(_root);
            List<string> list = RecentWorkDirs.Get();
            Assert.Single(list);
            Assert.Equal(_root, list[0]);
        }

        [Fact]
        public void Add_MostRecentFirst()
        {
            string a = Path.Combine(_root, "a"); Directory.CreateDirectory(a);
            string b = Path.Combine(_root, "b"); Directory.CreateDirectory(b);
            RecentWorkDirs.Add(a);
            RecentWorkDirs.Add(b);
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(2, list.Count);
            Assert.Equal(b, list[0]);
            Assert.Equal(a, list[1]);
        }

        [Fact]
        public void Add_DeduplicatesCaseInsensitiveAndTrailingSlash_MovesToFront()
        {
            string a = Path.Combine(_root, "a"); Directory.CreateDirectory(a);
            string b = Path.Combine(_root, "b"); Directory.CreateDirectory(b);
            RecentWorkDirs.Add(a);
            RecentWorkDirs.Add(b);
            // Re-add 'a' with a trailing slash and upper-cased — must collapse to one entry, now first.
            RecentWorkDirs.Add(a.ToUpperInvariant() + Path.DirectorySeparatorChar);
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(2, list.Count);
            Assert.Equal(a, list[0], StringComparer.OrdinalIgnoreCase);
            Assert.Equal(b, list[1], StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void Add_CapsAtFiveAndDropsOldest()
        {
            string[] dirs = new string[7];
            for (int i = 0; i < 7; i++)
            {
                dirs[i] = Path.Combine(_root, "d" + i);
                Directory.CreateDirectory(dirs[i]);
                RecentWorkDirs.Add(dirs[i]);
            }
            List<string> list = RecentWorkDirs.Get();
            Assert.Equal(RecentWorkDirs.MaxEntries, list.Count);
            Assert.Equal(dirs[6], list[0]); // newest first
            Assert.Equal(dirs[2], list[4]); // d0, d1 dropped
        }

        [Fact]
        public void Add_NullOrEmpty_IsIgnored()
        {
            RecentWorkDirs.Add(null);
            RecentWorkDirs.Add("");
            RecentWorkDirs.Add("   ");
            Assert.Empty(RecentWorkDirs.Get());
        }

        [Fact]
        public void List_RoundTripsAcrossCalls()
        {
            RecentWorkDirs.Add(_root);
            // A second independent Get reads back from disk.
            List<string> list = RecentWorkDirs.Get();
            Assert.Single(list);
            Assert.Equal(_root, list[0]);
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test GxPT.Tests/GxPT.Tests.csproj --filter "FullyQualifiedName~RecentWorkDirsTests"`
Expected: PASS, 7 tests passed.

(Because the implementation is created in Step 1, these pass on first run. If any fail, fix `RecentWorkDirs.cs` before continuing — do not edit the tests to match a bug.)

- [ ] **Step 5: Commit**

```bash
git add GxPT/Services/RecentWorkDirs.cs GxPT.Tests/GxPT.Tests.csproj GxPT.Tests/RecentWorkDirsTests.cs
git commit -m "Add RecentWorkDirs store with tests"
```

---

### Task 2: Add the "Open Recent Work Dir" menu item to the designer

**Files:**
- Modify: `GxPT/Forms/MainForm.Designer.cs` (instantiation ~line 36; `DropDownItems` list ~line 108; item config block ~after line 140; field declaration ~line 571)

- [ ] **Step 1: Instantiate the menu item**

In `MainForm.Designer.cs`, after the `miNewConversation` instantiation (line 36):

```csharp
            this.miNewConversation = new System.Windows.Forms.ToolStripMenuItem();
            this.miOpenRecentWorkDir = new System.Windows.Forms.ToolStripMenuItem();
```

- [ ] **Step 2: Add it to the File menu's DropDownItems**

In the `miFile.DropDownItems.AddRange(...)` array, insert after `this.miNewConversation,` (line 108):

```csharp
            this.miNewConversation,
            this.miOpenRecentWorkDir,
            this.miCloseConversation,
```

- [ ] **Step 3: Configure the item**

After the `miNewConversation` configuration block (immediately after line 140, `this.miNewConversation.Click += ...`):

```csharp
            // 
            // miOpenRecentWorkDir
            // 
            this.miOpenRecentWorkDir.Name = "miOpenRecentWorkDir";
            this.miOpenRecentWorkDir.Size = new System.Drawing.Size(221, 22);
            this.miOpenRecentWorkDir.Text = "Open Recent Work &Dir";
```

(No `Click` handler — this is a parent menu populated dynamically. Its children are rebuilt when the File menu opens, in Task 3.)

- [ ] **Step 4: Declare the field**

After the `miNewConversation` field declaration (line 571):

```csharp
        private System.Windows.Forms.ToolStripMenuItem miNewConversation;
        private System.Windows.Forms.ToolStripMenuItem miOpenRecentWorkDir;
```

- [ ] **Step 5: Build the app to verify the designer compiles**

Run: `msbuild GxPT/GxPT.csproj /p:Configuration=Debug /v:m` (or build `GxPT.sln` in VS2008).
Expected: Build succeeds. (If `msbuild` for .NET 3.5 is unavailable in this environment, this is verified during the manual run in Task 3 Step 6.)

- [ ] **Step 6: Commit**

```bash
git add GxPT/Forms/MainForm.Designer.cs
git commit -m "Add Open Recent Work Dir menu item to File menu"
```

---

### Task 3: Wire capture, dynamic population, and tab opening in MainForm

**Files:**
- Modify: `GxPT/Forms/MainForm.cs` (`SetWorkingFolderForContext` ~line 713; `ApplyLoadedWorkingDir` ~line 737; add new methods; wire `miFile.DropDownOpening`)

- [ ] **Step 1: Record the dir when the user explicitly picks a folder**

In `SetWorkingFolderForContext` (MainForm.cs), after `ctx.WorkingDir = dlg.SelectedPath;` (line 713, inside the `using` block):

```csharp
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ctx.WorkingDir = dlg.SelectedPath;
                RecentWorkDirs.Add(ctx.WorkingDir);
```

- [ ] **Step 2: Record the dir when a loaded conversation's working dir is adopted**

In `ApplyLoadedWorkingDir` (MainForm.cs ~line 740), after `ctx.WorkingDir` is assigned from the conversation:

```csharp
            ctx.WorkingDir = (ctx.Conversation != null) ? ctx.Conversation.WorkingDir : null;
            if (!string.IsNullOrEmpty(ctx.WorkingDir)) RecentWorkDirs.Add(ctx.WorkingDir);
```

- [ ] **Step 3: Add the tab-opening helper**

Add this method to `MainForm.cs` (place it next to `SetWorkingFolderForContext`, e.g. after `ClearWorkingFolderForContext` ~line 733):

```csharp
        // Opens a fresh conversation tab whose working folder is preset to 'dir'. Mirrors the
        // load flow (create tab -> set working dir -> persist -> adopt onto strip + MCP host),
        // and bumps 'dir' to the top of the recent list.
        private void OpenNewTabWithWorkingDir(string dir)
        {
            if (_tabManager == null || string.IsNullOrEmpty(dir)) return;
            TabManager.ChatTabContext ctx = _tabManager.CreateConversationTab();
            if (ctx == null) return;
            ctx.WorkingDir = dir;
            if (ctx.Conversation != null)
            {
                ctx.Conversation.WorkingDir = dir;
                ctx.Conversation.WorkspaceStripDismissed = false;
            }
            PersistWorkingDir(ctx);
            ApplyLoadedWorkingDir(ctx); // shows the workspace strip + binds MCP; also re-adds to recents
        }
```

(Note: `ApplyLoadedWorkingDir` already calls `RecentWorkDirs.Add` per Step 2, so opening a recent dir naturally bumps it to the front — no duplicate add needed here.)

- [ ] **Step 4: Add the submenu population handler**

Add this method to `MainForm.cs` (near the other menu handlers, e.g. after `miNewConversation_Click` ~line 2019):

```csharp
        // Rebuilds the "Open Recent Work Dir" submenu each time the File menu opens. Lists each
        // remembered dir (full path); missing dirs are shown disabled (grayed, "Folder not found"
        // tooltip) rather than dropped. Disables the parent only when there are no entries at all.
        private void PopulateRecentWorkDirsMenu()
        {
            if (miOpenRecentWorkDir == null) return;

            // Dispose and clear any previously-built child items.
            for (int i = miOpenRecentWorkDir.DropDownItems.Count - 1; i >= 0; i--)
            {
                System.Windows.Forms.ToolStripItem old = miOpenRecentWorkDir.DropDownItems[i];
                miOpenRecentWorkDir.DropDownItems.RemoveAt(i);
                old.Dispose();
            }

            int added = 0;
            List<string> dirs = RecentWorkDirs.Get();
            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;

                bool exists;
                try { exists = System.IO.Directory.Exists(dir); }
                catch { exists = false; }

                string captured = dir; // avoid closure capturing the loop variable
                System.Windows.Forms.ToolStripMenuItem item =
                    new System.Windows.Forms.ToolStripMenuItem(captured);
                if (exists)
                {
                    item.Click += delegate(object s, EventArgs e) { OpenNewTabWithWorkingDir(captured); };
                }
                else
                {
                    // Keep missing dirs visible but unselectable so the user can see them.
                    item.Enabled = false;
                    item.ToolTipText = "Folder not found";
                }
                miOpenRecentWorkDir.DropDownItems.Add(item);
                added++;
            }

            miOpenRecentWorkDir.Enabled = added > 0;
        }
```

(`List<string>` requires `using System.Collections.Generic;` — already present in MainForm.cs; verify and add if missing.)

- [ ] **Step 5: Wire the File menu's DropDownOpening event**

In `MainForm.cs`, find the form constructor or an init method where other event handlers are wired (search for an existing `this.miFile` reference or the constructor after `InitializeComponent()`), and add:

```csharp
            this.miFile.DropDownOpening += new EventHandler(this.miFile_DropDownOpening);
```

Then add the handler near `PopulateRecentWorkDirsMenu`:

```csharp
        private void miFile_DropDownOpening(object sender, EventArgs e)
        {
            PopulateRecentWorkDirsMenu();
        }
```

If no suitable post-`InitializeComponent()` location exists, wire it inside `MainForm.Designer.cs` instead, in the `miFile` configuration block (after line 118), matching the designer style:

```csharp
            this.miFile.DropDownOpening += new System.EventHandler(this.miFile_DropDownOpening);
```

- [ ] **Step 6: Build and manually verify**

Build `GxPT.sln` (VS2008 or msbuild v3.5) and run `GxPT.exe`. Verify:
1. With no recents yet, **File ▸ Open Recent Work Dir** is grayed out (disabled).
2. Set a working folder on a conversation (workspace strip "Set working folder", or the existing menu/route), then reopen File — the dir now appears under the submenu as a full path.
3. Click the entry → a new tab opens with the workspace strip showing that folder.
4. Set 6+ different folders → only the latest 5 appear, newest first; re-selecting an existing one moves it to the top without duplicating.
5. Delete one of the remembered folders on disk, reopen the menu → it still appears but is grayed/disabled (tooltip "Folder not found").

Run the unit suite to confirm nothing regressed:
Run: `dotnet test GxPT.Tests/GxPT.Tests.csproj`
Expected: PASS (all tests, including the 7 new ones).

- [ ] **Step 7: Commit**

```bash
git add GxPT/Forms/MainForm.cs GxPT/Forms/MainForm.Designer.cs
git commit -m "Wire Open Recent Work Dir: capture, populate, and open in new tab"
```

---

## Notes for the implementer

- **Order matters:** Task 1 (store) before Task 3 (which calls `RecentWorkDirs.Add`/`Get`). Task 2 (field + designer) before Task 3 Step 5 references `miOpenRecentWorkDir` / `miFile`.
- **C# 3.0 constraint** applies to `GxPT/Services/RecentWorkDirs.cs` and all `MainForm` edits (the `GxPT` assembly is .NET 3.5). `delegate(object s, EventArgs e) { ... }` anonymous methods are valid in C# 3.0; lambdas would also compile but anonymous-method syntax matches what the designer/codebase tends to use — either is fine.
- **Do not** add `RecentWorkDirs.Add` inside `OpenNewTabWithWorkingDir` separately; it would double-add (harmless due to dedup, but redundant). `ApplyLoadedWorkingDir` covers it.
- If `msbuild` for the net35 app isn't runnable in the worker's environment, the designer/MainForm build is validated during the Task 3 Step 6 manual run; flag this rather than skipping verification silently.
