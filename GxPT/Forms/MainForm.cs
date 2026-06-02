using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using GxPT;
using System.IO;
using Ionic.Zip;
using System.Reflection;
using iTextSharp.text.pdf;
using Parser = iTextSharp.text.pdf.parser;

namespace GxPT
{
    public partial class MainForm : Form
    {
        private OpenRouterClient _client;
        private McpHost _mcpHost;
        private McpToolRegistry _mcpRegistry;
        // Remembered tool approvals are shared across all tabs for the app session (in-memory). The
        // approval *prompt* is per-tab (each conversation has its own ToolApprovalPanel), but the
        // remembered choices live in this one store so "remember" applies everywhere.
        private IApprovalStore _approvalStore;
        // True only while restoring the previous session's tabs at startup. During restore, opening a
        // conversation marks its transcript for lazy build instead of building it immediately, so only
        // the visible tab is rendered up front (the rest hydrate when first selected).
        private bool _restoringTabs;
        private const string HelpApiKeysId = "help:api_keys";
        private const string HelpPrivacyId = "help:privacy";

        // Manager classes for UI concerns
        private SidebarManager _sidebarManager;
        private TabManager _tabManager;
        private ThemeManager _themeManager;
        private InputManager _inputManager;
        private AttachmentService _attachmentService;

        private bool _syncingModelCombo; // avoid event feedback loops when syncing combo text

        // Helper DTO for background-parsed messages
        private sealed class ParsedMessage
        {
            public MessageRole Role;
            public string Text;
            public List<Block> Blocks;
            public List<AttachedFile> Attachments; // optional
        }

        public MainForm()
        {
            InitializeComponent();
            InitializeManagers();
            HookEvents();
            InitializeDragAndDrop();
            InitializeClient();

            // Setup initial tab context for the designer-created tab
            SetupInitialConversationTab();
            _inputManager.SetHintText();

            // Wire banner link and ensure it lays out nicely
            if (this.lnkOpenSettings != null)
                this.lnkOpenSettings.LinkClicked += lnkOpenSettings_LinkClicked;
            if (this.pnlApiKeyBanner != null)
                this.pnlApiKeyBanner.Resize += (s, e) => LayoutApiKeyBanner();
            this.Load += (s, e) =>
            {
                UpdateApiKeyBanner();
                try { RestoreOpenTabsOnStartup(); }
                catch { }
                // After restoring tabs, if no API key is configured, open the API Keys Help tab and focus it
                try
                {
                    string k = AppSettings.GetString("openrouter_api_key");
                    if (k == null || k.Trim().Length == 0)
                    {
                        miApiKeysHelp_Click(this, EventArgs.Empty);
                    }
                }
                catch { }
            };
            this.FormClosing += MainForm_FormClosing_SaveOpenTabs;

            // Configure attachments banner container
            if (this.pnlAttachmentsBanner != null)
            {
                this.pnlAttachmentsBanner.AutoSize = false; // we'll manage height so it can grow with wrapping
                this.pnlAttachmentsBanner.Dock = DockStyle.Bottom;
                this.pnlAttachmentsBanner.Padding = new Padding(6, 3, 6, 3);
                this.pnlAttachmentsBanner.WrapContents = true;
                this.pnlAttachmentsBanner.AutoSizeMode = AutoSizeMode.GrowAndShrink;
                // Fixed darker color for contrast with input panel
                this.pnlAttachmentsBanner.BackColor = Color.DarkGray;
                // Recompute on container resize to grow for wrapped chips
                if (this.pnlBottom != null)
                    this.pnlBottom.SizeChanged += (s, e) => RebuildAttachmentsBanner();
            }

            // Kick off background prewarm of syntax highlighters to avoid UI stalls the first time
            // a code block is measured/drawn. Once warmed, mark ready and request a trivial reflow.
            try
            {
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        // Touch the SyntaxHighlighter static state
                        var langs = SyntaxHighlighter.GetSupportedLanguages();
                        if (langs != null && langs.Length > 0)
                        {
                            // Tokenize a tiny sample for a handful of common languages
                            string sample = "int x = 42; // warm\n";
                            for (int i = 0; i < langs.Length; i++)
                            {
                                string lang = langs[i];
                                if (string.IsNullOrEmpty(lang)) continue;
                                try { SyntaxHighlighter.Highlight(lang, sample); }
                                catch { }
                            }
                        }
                    }
                    catch { }
                    finally
                    {
                        try { SyntaxHighlightingRenderer.MarkHighlighterReady(); }
                        catch { }
                        // Ask active transcript to reflow once, now that highlighters are ready
                        try
                        {
                            if (IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                                    if (ctx != null && ctx.Transcript != null)
                                    {
                                        ctx.Transcript.RefreshTheme(); // triggers a reflow
                                    }
                                });
                            }
                        }
                        catch { }
                    }
                });
            }
            catch { }
        }

        // ===== Drag & Drop Attachments =====
        private void InitializeDragAndDrop()
        {
            try
            {
                // Enable on the form
                this.AllowDrop = true;
                this.DragEnter -= MainForm_DragEnter;
                this.DragEnter += MainForm_DragEnter;
                this.DragDrop -= MainForm_DragDrop;
                this.DragDrop += MainForm_DragDrop;

                // Also enable on key child controls so drops over them are handled too
                EnableDropOnControl(this.tabControl1);
                EnableDropOnControl(this.chatTranscript);
                EnableDropOnControl(this.splitContainer1);
                EnableDropOnControl(this.pnlBottom);
            }
            catch { }
        }

        private void EnableDropOnControl(Control c)
        {
            if (c == null) return;
            try
            {
                c.AllowDrop = true;
                c.DragEnter -= MainForm_DragEnter;
                c.DragEnter += MainForm_DragEnter;
                c.DragDrop -= MainForm_DragDrop;
                c.DragDrop += MainForm_DragDrop;
            }
            catch { }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            try
            {
                if (e != null && e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        EnsureAttachmentService();
                        for (int i = 0; i < files.Length; i++)
                        {
                            var f = files[i];
                            if (_attachmentService != null && _attachmentService.IsSupported(f))
                            { e.Effect = DragDropEffects.Copy; return; }
                        }
                    }
                }
            }
            catch { }
            e.Effect = DragDropEffects.None;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e == null || e.Data == null || !e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files == null || files.Length == 0) return;
                HandleDroppedFiles(files);
            }
            catch { }
        }

        private void HandleDroppedFiles(IEnumerable<string> paths)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;
            EnsureAttachmentService();
            List<string> skipped = new List<string>();
            var extracted = (_attachmentService != null) ? _attachmentService.ExtractMany(paths, out skipped) : new List<AttachedFile>();
            if (extracted != null && extracted.Count > 0)
            {
                if (ctx.PendingAttachments == null) ctx.PendingAttachments = new List<AttachedFile>();
                ctx.PendingAttachments.AddRange(extracted);
            }

            RebuildAttachmentsBanner();

            if (skipped != null && skipped.Count > 0)
            {
                try
                {
                    MessageBox.Show(this,
                        "Skipped unsupported items:\n - " + string.Join("\n - ", skipped.ToArray()),
                        "Attach Files",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                catch { }
            }
        }

        // Persist and restore open tabs (conversation IDs and active tab)
        private void MainForm_FormClosing_SaveOpenTabs(object sender, FormClosingEventArgs e)
        {
            try
            {
                var openIds = GetOpenConversationIdsInOrder();
                string activeId = GetActiveConversationId();
                SessionState.SaveOpenTabs(openIds, activeId);
            }
            catch { }

            // Shut down MCP servers (close stdio children / HTTP sessions).
            try { if (_mcpHost != null) _mcpHost.Dispose(); }
            catch { }
        }

        private List<string> GetOpenConversationIdsInOrder()
        {
            var list = new List<string>();
            try
            {
                if (this.tabControl1 == null) return list;
                foreach (TabPage page in this.tabControl1.TabPages)
                {
                    try
                    {
                        var ctx = _tabManager != null && page != null && _tabManager.TabContexts.ContainsKey(page)
                            ? _tabManager.TabContexts[page]
                            : null;
                        var id = ctx != null && ctx.Conversation != null ? ctx.Conversation.Id : null;
                        if (string.IsNullOrEmpty(id)) continue;
                        // Include help tabs (by known help ids) even if marked NoSaveUntilUserSend
                        if (ctx != null && ctx.NoSaveUntilUserSend && !IsHelpConversationId(id)) continue;
                        list.Add(id);
                    }
                    catch { }
                }
            }
            catch { }
            return list;
        }

        private string GetActiveConversationId()
        {
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null && ctx.Conversation != null) return ctx.Conversation.Id;
            }
            catch { }
            return null;
        }

        private void RestoreOpenTabsOnStartup()
        {
            try
            {
                List<string> ids; string activeFromFile;
                SessionState.LoadOpenTabs(out ids, out activeFromFile);
                if (ids == null || ids.Count == 0) return;

                // Defer transcript builds while every tab is opened, so reopening a session with many
                // tabs doesn't render them all up front. Only the tab left visible is built below; the
                // rest hydrate the first time they're selected (OnTabSelected -> HydrateTabIfNeeded).
                _restoringTabs = true;
                try
                {
                // We'll reuse the initial blank tab for the first item if suitable
                bool firstUsed = false;
                for (int i = 0; i < ids.Count; i++)
                {
                    string id = ids[i];
                    if (string.IsNullOrEmpty(id)) continue;
                    try
                    {
                        if (IsHelpConversationId(id))
                        {
                            // Reopen help tab from embedded resource by id
                            OpenHelpConversationById(id, ref firstUsed);
                        }
                        else
                        {
                            string path = ConversationStore.GetPathForId(id);
                            if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) continue;
                            var convo = ConversationStore.Load(_client, path);
                            if (convo == null) continue;

                            if (!firstUsed)
                            {
                                var blank = FindBlankTabPreferActive();
                                if (blank != null)
                                {
                                    OpenConversationInTab(blank, convo);
                                    firstUsed = true;
                                    continue;
                                }
                            }
                            OpenConversationInNewTab(convo);
                        }
                    }
                    catch { }
                }

                // Reselect previously active tab if still present
                try
                {
                    string active = activeFromFile;
                    if (!string.IsNullOrEmpty(active) && this.tabControl1 != null && _tabManager != null)
                    {
                        foreach (TabPage p in this.tabControl1.TabPages)
                        {
                            try
                            {
                                var ctx = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                                if (ctx != null && ctx.Conversation != null && string.Equals(ctx.Conversation.Id, active, StringComparison.Ordinal))
                                {
                                    this.tabControl1.SelectedTab = p;
                                    break;
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
                }
                finally { _restoringTabs = false; }

                // Build only the now-visible tab; the rest stay deferred until first selected.
                try { HydrateTabIfNeeded(_tabManager != null ? _tabManager.GetActiveContext() : null); }
                catch { }

                // Pre-warm MCP servers for the visible tab only (one reconcile, off the UI thread).
                // Other tabs' servers launch lazily when they're sent to or first selected.
                try { SyncMcpWorkingDirFromActiveTab(); }
                catch { }
            }
            catch { }
        }

        // Build a tab's transcript if it was deferred at session restore. Idempotent and cheap once
        // hydrated (the flag is cleared before the rebuild). Called for the visible tab after restore
        // and for each tab the first time it becomes active.
        private void HydrateTabIfNeeded(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || !ctx.NeedsTranscriptRebuild) return;
            ctx.NeedsTranscriptRebuild = false;
            if (ctx.Conversation != null) RebuildTranscriptAsync(ctx, ctx.Conversation);
        }

        private bool IsHelpConversationId(string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id)) return false;
                if (string.Equals(id, HelpApiKeysId, StringComparison.Ordinal)) return true;
                if (string.Equals(id, HelpPrivacyId, StringComparison.Ordinal)) return true;
                return id.StartsWith("help_", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        private void OpenHelpConversationById(string id, ref bool firstUsed)
        {
            try
            {
                Conversation convo = LoadHelpConversationById(id);
                if (convo == null) return;
                // Keep specialized help id to restore by id later
                // Mark as help/no-save until user sends
                var ctx = !firstUsed ? FindBlankTabPreferActive() : null;
                if (!firstUsed && ctx != null)
                {
                    ctx.NoSaveUntilUserSend = true;
                    OpenConversationInTab(ctx, convo);
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                    firstUsed = true;
                }
                else
                {
                    // New tab
                    var ctx2 = _tabManager != null ? _tabManager.CreateConversationTab() : null;
                    if (ctx2 == null) return;
                    ctx2.NoSaveUntilUserSend = true;
                    OpenConversationInTab(ctx2, convo);
                    try { if (ctx2.Transcript != null) ctx2.Transcript.ScrollToTop(); }
                    catch { }
                }
            }
            catch { }
        }

        private Conversation LoadHelpConversationById(string id)
        {
            try
            {
                string resourceName = null;
                if (string.Equals(id, HelpApiKeysId, StringComparison.Ordinal)) resourceName = "GxPT.Resources.Help.help_api_keys.json";
                else if (string.Equals(id, HelpPrivacyId, StringComparison.Ordinal)) resourceName = "GxPT.Resources.Help.help_privacy.json";
                else return null;

                var asm = typeof(MainForm).Assembly;
                using (var s = asm.GetManifestResourceStream(resourceName))
                {
                    if (s == null) return null;
                    using (var sr = new StreamReader(s, Encoding.UTF8, true))
                    {
                        string json = sr.ReadToEnd();
                        var convo = ConversationStore.LoadFromJson(_client, json);
                        return convo;
                    }
                }
            }
            catch { return null; }
        }

        private void InitializeManagers()
        {
            // Remembered approvals are kept only for the lifetime of the app (in-memory), not
            // persisted to settings.json — a remembered choice lasts the session, then resets. The
            // approval UI itself is per-tab: each conversation gets its own ToolApprovalPanel (see
            // AttachApprovalPanel), so a pending approval appears on the tab that requested it rather
            // than whatever tab happens to be active. The policy is built per turn in BeginToolSend
            // and bound to that tab's panel, all sharing this one store.
            try { _approvalStore = new InMemoryApprovalStore(); }
            catch { }

            // Initialize managers for UI concerns
            _sidebarManager = new SidebarManager(this, this.splitContainer1, this.miConversationHistory);
            _tabManager = new TabManager(this, this.tabControl1, this.msMain);
            _themeManager = new ThemeManager(this, this.chatTranscript, this.txtMessage,
                this.btnSend, this.cmbModel, this.lnkOpenSettings, this.lblNoApiKey);
            _inputManager = new InputManager(this, this.txtMessage, this.pnlInput,
                this.btnSend, this.cmbModel, this.splitContainer1, this.pnlApiKeyBanner, this.pnlAttachmentsBanner);

            // Wire manager events
            if (_tabManager != null)
            {
                _tabManager.TabSelected += OnTabSelected;
                _tabManager.TabsChanged += OnTabsChanged;
            }

            if (_sidebarManager != null)
            {
                _sidebarManager.SidebarToggled += OnSidebarToggled;
            }
        }

        private void HookEvents()
        {
            this.Resize += MainForm_Resize;

            // Wire menu items
            try
            {
                if (this.miNewConversation != null)
                {
                    this.miNewConversation.Click -= miNewConversation_Click;
                    this.miNewConversation.Click += miNewConversation_Click;
                }
            }
            catch { }
            try
            {
                if (this.miCloseConversation != null)
                {
                    this.miCloseConversation.Click -= closeConversationToolStripMenuItem_Click;
                    this.miCloseConversation.Click += closeConversationToolStripMenuItem_Click;
                }
            }
            catch { }

            try
            {
                if (this.cmbModel != null)
                {
                    this.cmbModel.SelectedIndexChanged -= cmbModel_SelectedIndexChanged;
                    this.cmbModel.SelectedIndexChanged += cmbModel_SelectedIndexChanged;
                    this.cmbModel.TextUpdate -= cmbModel_TextUpdate;
                    this.cmbModel.TextUpdate += cmbModel_TextUpdate;
                    // Adjust dropdown width dynamically to fit the widest item
                    this.cmbModel.DropDown -= cmbModel_DropDownAdjustWidth;
                    this.cmbModel.DropDown += cmbModel_DropDownAdjustWidth;
                }
            }
            catch { }

            try
            {
                if (this.btnAttach != null)
                {
                    this.btnAttach.Click -= btnAttach_Click;
                    this.btnAttach.Click += btnAttach_Click;
                }
            }
            catch { }
        }

        // Event handlers for manager events
        private void OnTabSelected(TabPage selectedTab)
        {
            // Build this tab's transcript if it was deferred at session restore (first time it's shown).
            // Skipped while the restore loop is still opening tabs — the visible one is built afterward.
            if (!_restoringTabs && _tabManager != null && selectedTab != null)
            {
                TabManager.ChatTabContext sel;
                if (_tabManager.TabContexts.TryGetValue(selectedTab, out sel)) HydrateTabIfNeeded(sel);
            }
            UpdateWindowTitleFromActiveTab();
            SyncComboModelFromActiveTab();
            // Refresh the attachments banner to reflect the active tab's pending attachments
            RebuildAttachmentsBanner();
            // Point the MCP host's workdir-scoped servers (files/git/command) at the active tab's folder.
            SyncMcpWorkingDirFromActiveTab();
            if (_inputManager != null) _inputManager.FocusInputSoon();
        }

        // Reconcile the MCP host's workdir-scoped servers (files/git/command) with the open tabs:
        // pre-warm the active conversation's folder and tear down any folder no longer referenced by an
        // open tab. Each distinct working directory runs its own server set, kept alive across tab
        // switches, so a tab is never disconnected by activity in another tab. Snapshots the tab state
        // on the UI thread, then does the (potentially blocking) connect/teardown off-thread. Called on
        // tab switch, tab open/close, and working-folder changes.
        private void SyncMcpWorkingDirFromActiveTab()
        {
            if (_mcpHost == null) return;
            // Don't launch MCP servers while restoring a session: opening each tab would otherwise
            // spin up a files/git/command set per distinct folder, and those process spawns/handshakes
            // saturate the thread pool and delay the visible tab's transcript from rendering. Servers
            // are launched lazily on first send (BeginToolSend) and pre-warmed once after restore.
            if (_restoringTabs) return;
            string active = null;
            var inUse = new List<string>();
            try
            {
                var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (act != null) active = act.WorkingDir;
                if (_tabManager != null)
                {
                    foreach (var c in _tabManager.TabContexts.Values)
                        if (c != null && !string.IsNullOrEmpty(c.WorkingDir)) inUse.Add(c.WorkingDir);
                }
            }
            catch { }

            McpHost hostRef = _mcpHost;
            string activeRef = active;
            string[] inUseArr = inUse.ToArray();
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    if (!string.IsNullOrEmpty(activeRef)) hostRef.EnsureWorkingDir(activeRef);
                    hostRef.RetainOnly(inUseArr); // release folders whose last tab closed
                }
                catch (Exception ex) { try { Logger.Log("mcp", "MCP workdir reconcile failed: " + ex.Message); } catch { } }
            });
        }

        // Creates the per-tab workspace strip docked above this tab's transcript, wiring its
        // Set/Change/Clear actions to that conversation's working folder. Called by TabManager when a
        // tab context is created.
        internal void AttachWorkspaceStrip(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                // Seed from the (possibly loaded) conversation so a re-opened chat keeps its folder.
                if (string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                    !string.IsNullOrEmpty(ctx.Conversation.WorkingDir))
                    ctx.WorkingDir = ctx.Conversation.WorkingDir;

                var strip = new WorkspaceContextStrip();
                ctx.WorkspaceStrip = strip;
                var ctxRef = ctx;
                strip.ChangeRequested += delegate { SetWorkingFolderForContext(ctxRef); };
                strip.ClearRequested += delegate { ClearWorkingFolderForContext(ctxRef); };
                strip.DismissRequested += delegate { DismissWorkspaceStripForContext(ctxRef); };
                // Dock order: the strip is Top, the approval panel is Bottom, and the transcript is
                // Fill. WinForms lays out docked controls by REVERSE z-order, so the Fill transcript
                // must be the frontmost child for it to fill the area *between* the Top strip and the
                // Bottom approval panel. Add the docked siblings, then send the transcript to front.
                ctx.Page.Controls.Add(strip);
                AttachApprovalPanel(ctx);
                if (ctx.Transcript != null) ctx.Transcript.BringToFront();
                strip.SetWorkingDir(ctx.WorkingDir);
                // Honor a persisted dismissal (only meaningful when no folder is set; setting one
                // re-shows the strip).
                if (string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                    ctx.Conversation.WorkspaceStripDismissed)
                    strip.Visible = false;
            }
            catch { }
        }

        // Create this tab's tool-approval panel (docked at the bottom of its transcript area, hidden
        // until a tool call awaits a decision). Each conversation gets its own panel so an approval is
        // shown on the tab that requested it; it reads file context from this tab's working dir.
        internal void AttachApprovalPanel(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null) return;
            try
            {
                var panel = new ToolApprovalPanel();
                ctx.ApprovalPanel = panel;
                var ctxRef = ctx;
                panel.WorkingDirProvider = delegate { return ctxRef.WorkingDir; };
                ctx.Page.Controls.Add(panel); // self-docks Bottom, starts hidden
            }
            catch { }
        }

        private void DismissWorkspaceStripForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            if (ctx.WorkspaceStrip != null) ctx.WorkspaceStrip.Visible = false;
            if (ctx.Conversation != null)
            {
                ctx.Conversation.WorkspaceStripDismissed = true;
                try { if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation); }
                catch { }
            }
        }

        // Entry point for the tab context menu: set the working folder for a specific tab. Useful
        // when the strip has been dismissed (setting a folder re-shows it).
        internal void SetWorkingFolderForTab(TabPage page)
        {
            if (_tabManager == null || page == null) return;
            TabManager.ChatTabContext ctx;
            if (!_tabManager.TabContexts.TryGetValue(page, out ctx) || ctx == null) return;
            SetWorkingFolderForContext(ctx);
        }

        private void SetWorkingFolderForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Select a working folder for file, git, and command tools in this conversation.";
                if (!string.IsNullOrEmpty(ctx.WorkingDir)) dlg.SelectedPath = ctx.WorkingDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                ctx.WorkingDir = dlg.SelectedPath;
            }
            // Setting a folder re-shows the strip, so a prior dismissal no longer applies.
            if (ctx.Conversation != null) ctx.Conversation.WorkspaceStripDismissed = false;
            PersistWorkingDir(ctx);
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(ctx.WorkingDir);
                ctx.WorkspaceStrip.Visible = true; // re-show if it had been dismissed
            }
            SyncMcpWorkingDirFromActiveTab();
        }

        private void ClearWorkingFolderForContext(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.WorkingDir = null;
            PersistWorkingDir(ctx);
            if (ctx.WorkspaceStrip != null) ctx.WorkspaceStrip.SetWorkingDir(null);
            SyncMcpWorkingDirFromActiveTab();
        }

        // After a conversation is loaded into a tab, adopt its persisted working folder onto the tab
        // context + strip and (re)bind the MCP host to it.
        private void ApplyLoadedWorkingDir(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.WorkingDir = (ctx.Conversation != null) ? ctx.Conversation.WorkingDir : null;
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(ctx.WorkingDir);
                // Hidden only if there's no folder AND the user previously dismissed it.
                bool dismissed = string.IsNullOrEmpty(ctx.WorkingDir) && ctx.Conversation != null &&
                                 ctx.Conversation.WorkspaceStripDismissed;
                ctx.WorkspaceStrip.Visible = !dismissed;
            }
            SyncMcpWorkingDirFromActiveTab();
        }

        // Mirror the tab's working folder onto its persisted conversation and save, so it re-opens
        // with the same folder. Skipped for brand-new, never-sent conversations (NoSaveUntilUserSend).
        private void PersistWorkingDir(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null) return;
            ctx.Conversation.WorkingDir = ctx.WorkingDir;
            try { if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation); }
            catch { }
        }

        private void OnTabsChanged()
        {
            if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
            // A tab opening/closing changes which working folders are still in use; release the
            // scoped servers of any folder whose last tab just closed.
            SyncMcpWorkingDirFromActiveTab();
        }

        private void OnSidebarToggled()
        {
            if (_themeManager != null) _themeManager.ApplyFontSizeSettingToAllUi();
        }

        // Public methods for managers to call back
        internal OpenRouterClient GetClient()
        {
            return _client;
        }

        internal MenuStrip GetMenuStrip()
        {
            return this.msMain;
        }

        internal SidebarManager GetSidebarManager()
        {
            return _sidebarManager;
        }

        internal TabManager GetTabManager()
        {
            return _tabManager;
        }

        internal ThemeManager GetThemeManager()
        {
            return _themeManager;
        }

        internal InputManager GetInputManager()
        {
            return _inputManager;
        }

        // Allow TabManager to force-refresh the attachments banner after seeding attachments
        internal void RefreshAttachmentsBannerUi()
        {
            try { RebuildAttachmentsBanner(); }
            catch { }
        }

        internal void SelectTab(TabPage page)
        {
            if (_tabManager != null) _tabManager.SelectTab(page);
        }

        internal void CloseTab(TabPage page)
        {
            if (_tabManager != null) _tabManager.CloseConversationTab(page);
        }

        internal void UntrackOpenConversation(TabPage page)
        {
            if (_sidebarManager != null) _sidebarManager.UntrackOpenConversation(page);
        }

        internal void UpdateWindowTitle()
        {
            UpdateWindowTitleFromActiveTab();
        }

        internal void ApplyFontSetting(ChatTranscriptControl transcript)
        {
            if (_themeManager != null) _themeManager.ApplyFontSetting(transcript);
        }

        internal void EnsureConversationId(Conversation conversation)
        {
            ConversationStore.EnsureConversationId(conversation);
        }

        // Exit edit mode and restore compose UI (used for ESC and unchanged sends)
        internal void CancelEditingAndRestoreConversation()
        {
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx == null) return;

                ctx.PendingEditActive = false;
                ctx.PendingEditIndex = -1;
                ctx.PendingEditOriginalModel = null;

                // Clear attachments and refresh banner
                ClearAttachmentsBanner();

                // Clear input and restore hint
                if (_inputManager != null)
                {
                    _inputManager.ClearInput();
                    _inputManager.SetHintText();
                    _inputManager.FocusInputSoon();
                }
            }
            catch { }
        }

        internal void OpenConversation(Conversation convo)
        {
            // Prefer reusing a blank tab (no messages). If none, open a new tab.
            var blank = FindBlankTabPreferActive();
            if (blank != null)
            {
                OpenConversationInTab(blank, convo);
            }
            else
            {
                OpenConversationInNewTab(convo);
            }
        }

        public string GetSelectedModel()
        {
            try
            {
                string m = (cmbModel != null ? cmbModel.Text : null) ?? string.Empty;
                m = m.Trim();
                return string.IsNullOrEmpty(m) ? "openai/gpt-4o" : m;
            }
            catch
            {
                return "openai/gpt-4o";
            }
        }

        private void InitializeClient()
        {
            // Read API key from settings.json in %AppData%\GxPT
            string apiKey = AppSettings.GetString("openrouter_api_key");
            // curl.exe expected next to executable (bin/Debug)
            string curlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe");
            _client = new OpenRouterClient(apiKey, curlPath);
            // Populate models from settings and reflect configuration
            PopulateModelsFromSettings();
            UpdateApiKeyBanner();
            if (_themeManager != null) _themeManager.ApplyFontSizeSettingToAllUi();
            // Apply theme across existing transcripts and primary UI
            try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
            catch { }

            // Sync Dark Mode menu checked state from settings
            try
            {
                if (this.miDarkMode != null)
                {
                    string theme = AppSettings.GetString("theme") ?? "light";
                    bool isDark = theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                    this.miDarkMode.Checked = isDark;
                }
            }
            catch { }

            // Apply transcript/message width settings to existing transcripts
            try
            {
                var widths = TranscriptWidthSettings.Resolve();
                // Primary designer transcript
                widths.ApplyTo(this.chatTranscript);
                // All tab transcripts
                if (_tabManager != null)
                {
                    foreach (var kv in _tabManager.TabContexts)
                        widths.ApplyTo(kv.Value != null ? kv.Value.Transcript : null);
                }
            }
            catch { }

            // (Re)build the MCP host from the current settings (toggles, web-search key, GitHub PAT,
            // and mcp.json custom servers). Connecting happens on a background thread.
            RebuildMcpHost();
        }

        // Assembles MCP server specs from settings and (re)starts the host. Safe to call repeatedly
        // (e.g. after Settings is saved). Workdir-independent servers (web + GitHub + custom) connect
        // here; files/git/command are deferred until a working-directory UX exists.
        private void RebuildMcpHost()
        {
            try
            {
                // Tear down any previous host (servers from the old settings).
                if (_mcpHost != null)
                {
                    try { _mcpHost.Dispose(); }
                    catch { }
                    _mcpHost = null;
                }

                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string curlPath = System.IO.Path.Combine(baseDir, "Lib\\curl.exe");
                string caBundle = System.IO.Path.Combine(baseDir, "Lib\\curl-ca-bundle.crt");

                var opts = new McpConfig.BuiltInOptions();
                opts.WebEnabled = AppSettings.GetBool("mcp_web_enabled", false);
                opts.FilesEnabled = AppSettings.GetBool("mcp_files_enabled", false);
                // Git defaults ON when git is installed and OFF when it isn't, and is force-disabled
                // when git is missing regardless of the stored setting — so we never launch a git
                // server whose tools could only return "git not found".
                opts.GitEnabled = GitProbe.IsInstalled() && AppSettings.GetBool("mcp_git_enabled", true);
                opts.CommandEnabled = AppSettings.GetBool("mcp_command_enabled", false);
                opts.WebSearchKey = AppSettings.GetString("mcp_websearch_key");
                opts.CurlPath = curlPath;
                // Server exes: dev builds deploy them to a 'mcp-servers' subfolder (AfterBuild copy);
                // the installer lays them flat beside GxPT.exe (so VS dedupes the shared Mcp35/
                // Newtonsoft DLLs). Prefer the subfolder when present, else use the app dir.
                string serverSubdir = System.IO.Path.Combine(baseDir, "mcp-servers");
                opts.ServerDir = System.IO.Directory.Exists(serverSubdir) ? serverSubdir : baseDir;

                var specs = new List<McpServerSpec>(McpConfig.BuiltInSpecs(opts));
                specs.Add(McpConfig.GitHubSpec(
                    AppSettings.GetBool("mcp_github_enabled", false),
                    AppSettings.GetString("mcp_github_pat")));

                // Custom servers from mcp.json (GitHub is configured above, not here).
                try
                {
                    string mcpFile = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "GxPT\\mcp.json");
                    if (System.IO.File.Exists(mcpFile))
                    {
                        string mcpText = System.IO.File.ReadAllText(mcpFile);
                        specs.AddRange(McpConfig.ParseUserServers(mcpText, LoggerSink.Instance));
                    }
                }
                catch { }

                var clientInfo = new Mcp35.Core.Protocol.Implementation();
                clientInfo.Name = "GxPT";
                clientInfo.Version = "1.0";

                _mcpRegistry = new McpToolRegistry(McpToolRegistry.DefaultRevealCap, LoggerSink.Instance);
                var connector = new DefaultServerConnector(clientInfo, curlPath, caBundle, LoggerSink.Instance);
                _mcpHost = new McpHost(connector, _mcpRegistry, LoggerSink.Instance);

                // Snapshot the active tab's working folder now (on the UI thread) so the rebuilt host
                // re-binds its workdir-scoped servers (files/git/command) to it. Without this, a host
                // rebuilt after a Settings save starts with no active workdir and those servers never
                // launch even though the current conversation has a folder set.
                string activeWorkdir = null;
                try
                {
                    var actCtx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                    if (actCtx != null) activeWorkdir = actCtx.WorkingDir;
                }
                catch { }

                // Connecting (handshakes / process spawns) can block — do it off the UI thread.
                McpHost hostRef = _mcpHost;
                string wd = activeWorkdir;
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        hostRef.Start(specs);
                        // Launch the workdir-scoped servers for the active conversation's folder.
                        if (!string.IsNullOrEmpty(wd)) hostRef.EnsureWorkingDir(wd);
                    }
                    catch (Exception ex) { try { Logger.Log("mcp", "host start failed: " + ex.Message); } catch { } }
                });
            }
            catch (Exception ex)
            {
                try { Logger.Log("mcp", "RebuildMcpHost failed: " + ex.Message); }
                catch { }
            }
        }

        // Build a context for the existing designer tab (tabPage1 + chatTranscript)
        private void SetupInitialConversationTab()
        {
            try
            {
                if (this.tabControl1 == null || this.tabPage1 == null || this.chatTranscript == null)
                    return;

                var ctx = _tabManager != null ? _tabManager.SetupInitialConversationTab(this.tabPage1, this.chatTranscript) : null;
                if (ctx != null)
                {
                    if (_sidebarManager != null && ctx.Conversation != null)
                        _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);
                    SyncComboModelFromActiveTab();
                    if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();

                    // Ensure initial transcript theme is applied on launch
                    try { if (ctx.Transcript != null) ctx.Transcript.RefreshTheme(); }
                    catch { }
                }
            }
            catch { }
        }

        private void UpdateWindowTitleFromActiveTab()
        {
            try
            {
                var page = (this.tabControl1 != null ? this.tabControl1.SelectedTab : null);
                string name = (page != null ? page.Text : null);
                this.Text = string.IsNullOrEmpty(name) ? "GxPT" : ("GxPT - " + name);
            }
            catch { }
        }

        private void miSettings_Click(object sender, EventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                dlg.ShowDialog(this);
                // The dialog writes settings.json directly; drop the cached copy so reads are fresh.
                AppSettings.Reload();
                // Re-init client in case API key changed
                InitializeClient();
                UpdateApiKeyBanner();
                // Theme may have changed; re-apply to all open transcripts
                try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
                catch { }
                // Re-apply transcript/message widths in case they changed
                try
                {
                    var widths = TranscriptWidthSettings.Resolve();
                    widths.ApplyTo(this.chatTranscript);
                    if (_tabManager != null)
                    {
                        foreach (var kv in _tabManager.TabContexts)
                            widths.ApplyTo(kv.Value != null ? kv.Value.Transcript : null);
                    }
                }
                catch { }
            }
        }

        // ===== Export/Import conversations (DotNetZip) =====
        // Legacy path resolver no longer needed; logic consolidated in ImportExportService

        // Designer wires this
        private void miExport_Click(object sender, EventArgs e)
        {
            ImportExportManager.ExportAll(this);
        }

        // Designer wires this
        private void miImport_Click(object sender, EventArgs e)
        {
            var ok = ImportExportManager.ImportAll(this);
            if (ok)
            {
                try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                catch { }
            }
        }

        // Called by Program.cs after the form is shown when the app is launched by double-clicking a .gxpt/.gxcv file
        public void ImportArchiveFromShell(string archivePath)
        {
            if (string.IsNullOrEmpty(archivePath)) return;
            try
            {
                if (!System.IO.File.Exists(archivePath)) return;

                // Warn about overwrite if there are existing conversations
                try
                {
                    if (ConversationStore.ListAll().Count > 0)
                    {
                        var dr = MessageBox.Show(this,
                            "Importing will overwrite existing files with the same names. Continue?",
                            "Import Conversations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (dr != DialogResult.Yes) return;
                    }
                }
                catch { }

                string targetDir = ImportExportService.GetConversationsFolderPath();
                try { System.IO.Directory.CreateDirectory(targetDir); }
                catch { }

                try
                {
                    ImportExportService.ImportAll(archivePath, targetDir, true);
                    try { MessageBox.Show(this, "Import completed.", "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information); }
                    catch { }
                    try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                    catch { }
                }
                catch (Exception ex)
                {
                    try { MessageBox.Show(this, "Import failed: " + ex.Message, "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error); }
                    catch { }
                }
            }
            catch { }
        }

        private void miExit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;

            // Validate input
            string baseText = _inputManager != null ? (_inputManager.GetInputText() ?? string.Empty) : string.Empty;
            string text = baseText;
            if (ctx.IsSending) return; // ensure only one in-flight request per tab

            // If editing a prior user message, compare text+attachments+model; confirm only if changed
            bool isEditResend = false;
            if (ctx.PendingEditActive && ctx.PendingEditIndex >= 0)
            {
                int cutIndex = ctx.PendingEditIndex;
                if (cutIndex >= 0 && cutIndex < ctx.Conversation.History.Count)
                {
                    var orig = ctx.Conversation.History[cutIndex];
                    if (orig != null && string.Equals(orig.Role, "user", StringComparison.OrdinalIgnoreCase))
                    {
                        // Compare text
                        bool sameText = string.Equals(orig.Content ?? string.Empty, text ?? string.Empty, StringComparison.Ordinal);
                        // Compare attachments
                        var pending = ctx.PendingAttachments ?? new List<AttachedFile>();
                        var origAtt = orig.Attachments ?? new List<AttachedFile>();
                        bool sameAtt = AreAttachmentsEqual(origAtt, pending);
                        // Compare model (changed model should allow resend/reset)
                        string modelAtEditStart = ctx.PendingEditOriginalModel ?? ctx.SelectedModel;
                        string currentModel = ctx.SelectedModel;
                        bool sameModel = string.Equals(modelAtEditStart ?? string.Empty, currentModel ?? string.Empty, StringComparison.Ordinal);
                        if (sameText && sameAtt && sameModel)
                        {
                            // Nothing changed: exit edit mode and restore to un-edited state
                            try { CancelEditingAndRestoreConversation(); }
                            catch { }
                            return;
                        }

                        // Confirm reset
                        var dr2 = MessageBox.Show(this,
                            "You are editing a previous user message. The conversation will be reset back to that point. Continue?",
                            "Reset Conversation?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                        if (dr2 != DialogResult.OK) return;

                        try
                        {
                            // Replace content and attachments, then truncate after
                            orig.Content = text;
                            if (pending != null && pending.Count > 0)
                            {
                                orig.Attachments = new List<AttachedFile>();
                                for (int i = 0; i < pending.Count; i++)
                                {
                                    var af = pending[i]; if (af == null) continue;
                                    orig.Attachments.Add(new AttachedFile(af.FileName, af.Content));
                                }
                            }
                            else
                            {
                                orig.Attachments = null;
                            }

                            while (ctx.Conversation.History.Count > cutIndex + 1)
                                ctx.Conversation.History.RemoveAt(ctx.Conversation.History.Count - 1);

                            // Rebuild transcript UI from truncated conversation (batched)
                            ctx.Transcript.ClearMessages();
                            ctx.Transcript.BeginBatchUpdates();
                            try
                            {
                                foreach (var m in ctx.Conversation.History)
                                {
                                    if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                                        ctx.Transcript.AddMessage(MessageRole.Assistant, m.Content);
                                    else if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                                    {
                                        if (m.Attachments != null && m.Attachments.Count > 0)
                                            ctx.Transcript.AddMessage(MessageRole.User, m.Content, m.Attachments);
                                        else
                                            ctx.Transcript.AddMessage(MessageRole.User, m.Content);
                                    }
                                }
                            }
                            finally
                            {
                                ctx.Transcript.EndBatchUpdates(true);
                            }

                            // Reset edit state and mark as edit resend path
                            ctx.PendingEditActive = false; ctx.PendingEditIndex = -1; ctx.PendingEditOriginalModel = null; isEditResend = true;
                            // Clear pending attachments UI for a clean send path
                            ClearAttachmentsBanner();
                        }
                        catch { }
                    }
                }
            }

            // For normal sends, block empty input; allow empty when re-sending an edit (conversation already has the message)
            if (!isEditResend && text.Length == 0) return;

            // Ensure client configured before we lock sending
            if (_client == null || !_client.IsConfigured)
            {
                string reason = _client == null
                    ? "Client not initialized."
                    : (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Lib\\curl.exe"))
                        ? "curl.exe could not be found."
                        : "Missing API key in settings.");
                // Show error as assistant bubble (no placeholder first)
                ctx.Transcript.AddMessage(MessageRole.Assistant, "Error: " + reason);
                Logger.Log("Send", "Blocked: " + reason);
                return;
            }

            // Lock sending immediately to avoid duplicate sends from rapid clicks/Enter
            ctx.IsSending = true;

            try
            {
                Logger.Log("Send", "Begin send. Model=" + GetSelectedModel());

                // Snapshot pending attachments, but do not append into stored history content
                List<AttachedFile> attachmentsSnapshot = null;
                string textForTranscript = baseText; // UI shows only user-entered text
                if (ctx.PendingAttachments != null && ctx.PendingAttachments.Count > 0)
                {
                    attachmentsSnapshot = new List<AttachedFile>(ctx.PendingAttachments.Count);
                    for (int i = 0; i < ctx.PendingAttachments.Count; i++)
                    {
                        var af = ctx.PendingAttachments[i];
                        if (af == null) continue;
                        attachmentsSnapshot.Add(new AttachedFile(af.FileName, af.Content));
                    }
                }
                if (!isEditResend)
                {
                    // Normal path: append a new user message to transcript and history
                    if (attachmentsSnapshot != null && attachmentsSnapshot.Count > 0)
                        ctx.Transcript.AddMessage(MessageRole.User, textForTranscript, attachmentsSnapshot);
                    else
                        ctx.Transcript.AddMessage(MessageRole.User, textForTranscript);
                    // Store in conversation history
                    ctx.Conversation.AddUserMessage(baseText);
                    try
                    {
                        if (attachmentsSnapshot != null && attachmentsSnapshot.Count > 0)
                        {
                            var last = ctx.Conversation.History.Count > 0 ? ctx.Conversation.History[ctx.Conversation.History.Count - 1] : null;
                            if (last != null && string.Equals(last.Role, "user", StringComparison.OrdinalIgnoreCase))
                                last.Attachments = attachmentsSnapshot;
                        }
                    }
                    catch { }
                }
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                // If this tab was a help template, convert to a standard conversation id now
                if (ctx.NoSaveUntilUserSend)
                {
                    try
                    {
                        if (IsHelpConversationId(ctx.Conversation.Id))
                        {
                            // Clear id so a new standard id is generated on save
                            ctx.Conversation.Id = null;
                            ConversationStore.EnsureConversationId(ctx.Conversation);
                            // Update sidebar tracking to reflect new id
                            if (_sidebarManager != null) _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);
                        }
                    }
                    catch { }
                    ctx.NoSaveUntilUserSend = false;
                }
                ConversationStore.Save(ctx.Conversation); // save when a new user message starts/continues a convo
                Logger.Log("Send", "User message added. HistoryCount=" + ctx.Conversation.History.Count);
                if (_inputManager != null)
                {
                    _inputManager.ClearInput();
                    // Ensure the input panel is sized correctly immediately after send
                    try { _inputManager.AdjustInputBoxHeight(); }
                    catch { }
                }
                ClearAttachmentsBanner();

                var modelToUse = GetSelectedModel();

                // Tool-enabled turn: tool activity renders as a separate chrome-less message above the
                // answer bubble. BeginToolSend owns the whole turn; the plain path below is unchanged.
                if (_mcpRegistry != null && _mcpRegistry.HasTools)
                {
                    BeginToolSend(ctx, modelToUse);
                    return;
                }

                // Add placeholder assistant message to stream into and capture its index
                int assistantIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Assistant, string.Empty);
                Logger.Log("Send", "Assistant placeholder index=" + assistantIndex);
                var assistantBuilder = new StringBuilder();

                // Throttle UI updates with a WinForms timer (coalesces rapid deltas)
                var sbLock = new object();
                int lastRenderedLen = 0;
                var renderTimer = new System.Windows.Forms.Timer();
                renderTimer.Interval = 75; // ~13 fps; adjust between 50-150ms if needed
                renderTimer.Tick += (s2, e2) =>
                {
                    string snapshot;
                    int len;
                    lock (sbLock)
                    {
                        len = assistantBuilder.Length;
                        if (len == lastRenderedLen) return; // nothing new
                        snapshot = assistantBuilder.ToString();
                        lastRenderedLen = len;
                    }
                    // Update on UI thread (timer runs on UI thread)
                    ctx.Transcript.UpdateMessageAt(assistantIndex, snapshot);
                };
                // Keep view pinned to bottom while streaming
                try { ctx.Transcript.StickToBottomDuringStreaming = true; }
                catch { }
                renderTimer.Start();

                // Kick off streaming in background
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        // Snapshot the history and log it
                        // Build a model snapshot where attachments are appended to content on-the-fly
                        var snapshot = BuildMessagesForModel(ctx.Conversation.History);
                        try
                        {
                            var sbSnap = new StringBuilder();
                            sbSnap.Append("Sending ").Append(snapshot.Count).Append(" messages:\n");
                            for (int i = 0; i < snapshot.Count; i++)
                            {
                                var m = snapshot[i];
                                string content = m.Content ?? string.Empty;
                                content = content.Replace("\r", " ").Replace("\n", " ");
                                if (content.Length > 200) content = content.Substring(0, 200) + "...";
                                sbSnap.Append("  ").Append(i).Append(": ").Append(m.Role).Append(" | ").Append(content).Append('\n');
                            }
                            Logger.Log("Send", sbSnap.ToString());
                        }
                        catch { }

                        // Determine provider data collection preference from settings: if text contains "Not" then false, else true
                        bool providerAllow = true;
                        try
                        {
                            // Prefer boolean setting
                            providerAllow = AppSettings.GetBool("provider_data_collection", true);
                        }
                        catch
                        {
                            // Fallback to string combobox text semantics if needed
                            try
                            {
                                string pdc = AppSettings.GetString("provider_data_collection");
                                if (!string.IsNullOrEmpty(pdc))
                                    providerAllow = pdc.IndexOf("Not", StringComparison.OrdinalIgnoreCase) < 0;
                            }
                            catch { providerAllow = true; }
                        }

                        _client.CreateCompletionStream(
                            modelToUse,
                            snapshot,
                            new ClientProperties { Stream = true, ProviderDataCollectionAllowed = providerAllow },
                            delegate(string d)
                            {
                                if (string.IsNullOrEmpty(d)) return;
                                lock (sbLock) { assistantBuilder.Append(d); }
                                // no per-delta BeginInvoke; timer will render
                            },
                            delegate
                            {
                                // finalize on UI thread (update history and unlock send)
                                string finalText;
                                lock (sbLock) { finalText = assistantBuilder.ToString(); }
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    try { renderTimer.Stop(); renderTimer.Dispose(); }
                                    catch { }
                                    try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                                    catch { }
                                    ctx.Transcript.UpdateMessageAt(assistantIndex, finalText);
                                    ctx.Conversation.AddAssistantMessage(finalText);
                                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                                    // Save assistant completion if allowed
                                    if (!ctx.NoSaveUntilUserSend)
                                        ConversationStore.Save(ctx.Conversation); // save only after streaming completes
                                    try { Logger.Log("Transcript", "Final assistant message at index=" + assistantIndex + ":\n" + (finalText ?? string.Empty)); }
                                    catch { }
                                    Logger.Log("Send", "Assistant finalized at index=" + assistantIndex + ", chars=" + (finalText != null ? finalText.Length : 0));
                                    ctx.IsSending = false;
                                    if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                                });
                            },
                            delegate(string err)
                            {
                                if (string.IsNullOrEmpty(err)) err = "Unknown error.";
                                string partial;
                                lock (sbLock) { partial = assistantBuilder.ToString(); }
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    try { renderTimer.Stop(); renderTimer.Dispose(); }
                                    catch { }
                                    try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                                    catch { }
                                    // Keep any partial text in the assistant bubble; otherwise drop the
                                    // empty placeholder so only the red error notice shows.
                                    if (partial.Trim().Length > 0)
                                        ctx.Transcript.UpdateMessageAt(assistantIndex, partial);
                                    else if (assistantIndex == ctx.Transcript.MessageCount - 1)
                                        ctx.Transcript.RemoveLastMessage();
                                    ShowTranscriptError(ctx.Transcript, err);
                                    Logger.Log("Send", "Stream error at index=" + assistantIndex + ": " + err);
                                    ctx.IsSending = false;
                                    // don't save on failure; no new assistant content
                                });
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        string partial;
                        lock (sbLock) { partial = assistantBuilder.ToString(); }
                        BeginInvoke((MethodInvoker)delegate
                        {
                            try { renderTimer.Stop(); renderTimer.Dispose(); }
                            catch { }
                            try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                            catch { }
                            if (partial.Trim().Length > 0)
                                ctx.Transcript.UpdateMessageAt(assistantIndex, partial);
                            else if (assistantIndex == ctx.Transcript.MessageCount - 1)
                                ctx.Transcript.RemoveLastMessage();
                            ShowTranscriptError(ctx.Transcript, ex.Message);
                            Logger.Log("Send", "Exception in streaming worker: " + ex.Message);
                            ctx.IsSending = false;
                        });
                    }
                });
            }
            catch
            {
                Logger.Log("Send", "Send failed unexpectedly; unlocking.");
                ctx.IsSending = false;
                throw;
            }
        }

        // Owns a tool-enabled chat turn. Tool activity streams into a chrome-less "Tool" message and
        // the model's answer into a separate Assistant bubble below it. Both bubbles are created
        // lazily (in the render timer) so they appear in the order content arrives — tool calls
        // first, then the answer. Runs the orchestrator on a worker thread; it appends the structured
        // assistant/tool messages to history itself. Called on the UI thread.
        // One ordered segment of a streaming tool turn: either an assistant-text bubble or a
        // chrome-less tool-activity block. Text is appended on the orchestrator worker thread (under
        // the turn's lock); Index/LastLen are assigned on the UI thread when first rendered.
        private sealed class LiveSeg
        {
            public readonly MessageRole Role;
            public readonly StringBuilder Text = new StringBuilder();
            public int Index;   // transcript message index, -1 until materialized
            public int LastLen; // last rendered text length (UI thread only)
            public LiveSeg(MessageRole role) { Role = role; Index = -1; LastLen = -1; }
        }

        private void BeginToolSend(TabManager.ChatTabContext ctx, string model)
        {
            // Ordered transcript segments for this turn: assistant-text bubbles and chrome-less
            // tool-activity blocks, interleaved in arrival order so the view reads chronologically
            // (text, then the tools it triggered, then the next text, ...). A segment only becomes a
            // real transcript message once it has content, so a turn that errors before producing any
            // output never leaves an empty placeholder bubble behind.
            var sbLock = new object();
            var segs = new List<LiveSeg>();

            var renderTimer = new System.Windows.Forms.Timer();
            renderTimer.Interval = 75;
            renderTimer.Tick += delegate { MaterializeLiveSegs(ctx, sbLock, segs); };
            try { ctx.Transcript.StickToBottomDuringStreaming = true; }
            catch { }
            renderTimer.Start();

            bool providerAllow = true;
            try { providerAllow = AppSettings.GetBool("provider_data_collection", true); }
            catch { providerAllow = true; }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Make sure THIS conversation's folder has its own scoped server set before the
                    // turn runs (idempotent; no-op if already connected), and route the turn's tool
                    // calls to that folder so a concurrent turn in another tab can't interfere.
                    if (_mcpHost != null && !string.IsNullOrEmpty(ctx.WorkingDir))
                        _mcpHost.EnsureWorkingDir(ctx.WorkingDir);

                    // Per-turn approval policy bound to THIS tab's panel, so the prompt appears on the
                    // conversation that requested the tool. The remembered-choice store is shared
                    // across tabs. Falls back to allow-all only if approvals couldn't be set up.
                    IToolApprovalPolicy approval = (_approvalStore != null && ctx.ApprovalPanel != null)
                        ? new ToolApprovalPolicy(
                            new ToolClassifier(),
                            new TranscriptApprovalPrompt(this, delegate { return ctx.ApprovalPanel; }),
                            _approvalStore)
                        : (IToolApprovalPolicy)new AllowAllApprovalPolicy();
                    var orch = new McpChatOrchestrator(_client, _mcpRegistry, approval,
                                                       model, LoggerSink.Instance);
                    orch.WorkingDir = ctx.WorkingDir;
                    orch.ProviderDataCollectionAllowed = providerAllow;
                    orch.RequestMessageTransform = delegate(IList<ChatMessage> h)
                    {
                        List<ChatMessage> asList = h as List<ChatMessage>;
                        if (asList == null) asList = new List<ChatMessage>(h);
                        return BuildMessagesForModel(asList);
                    };

                    // Assistant text appends to the current assistant bubble; a tool call closes it so
                    // the next run of text starts a fresh bubble below the tool record. Inter-turn
                    // whitespace (a stray newline some models emit before the next tool call) must NOT
                    // open a bubble — otherwise it splits two adjacent tool blocks with an empty-looking
                    // gap instead of letting them merge into one tight block.
                    Action<string> onAppend = delegate(string t)
                    {
                        if (string.IsNullOrEmpty(t)) return;
                        lock (sbLock)
                        {
                            LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                            if (cur != null && cur.Role == MessageRole.Assistant)
                            {
                                cur.Text.Append(t); // continuation — keep internal whitespace
                                return;
                            }
                            if (t.Trim().Length == 0) return; // don't open a bubble on whitespace alone
                            cur = new LiveSeg(MessageRole.Assistant);
                            segs.Add(cur);
                            cur.Text.Append(t);
                        }
                    };
                    // argsJson is threaded through for files__edit etc., which render a collapsible
                    // record instead of the generic "using" marker. Register the record (it has its own
                    // lock) before taking sbLock. Live keys are per-call GUIDs (ephemeral — the reloaded
                    // view re-derives under the persisted call id). Consecutive calls share one block.
                    Action<string, string> onToolCall = delegate(string name, string argsJson)
                    {
                        string marker = EditDiffMarkerOrCall(ctx.Transcript, name, argsJson, Guid.NewGuid().ToString("N"));
                        lock (sbLock)
                        {
                            LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                            if (cur == null || cur.Role != MessageRole.Tool)
                            {
                                cur = new LiveSeg(MessageRole.Tool);
                                segs.Add(cur);
                            }
                            if (cur.Text.Length > 0) cur.Text.Append("\r\n");
                            cur.Text.Append(marker);
                        }
                    };
                    Action onComplete = delegate
                    {
                        BeginInvoke((MethodInvoker)delegate
                        { FinalizeToolSend(ctx, renderTimer, sbLock, segs, null); });
                    };
                    Action<string> onErr = delegate(string err)
                    {
                        string e2 = string.IsNullOrEmpty(err) ? "Unknown error." : err;
                        BeginInvoke((MethodInvoker)delegate
                        { FinalizeToolSend(ctx, renderTimer, sbLock, segs, e2); });
                    };

                    orch.RunTurn(ctx.Conversation.History, new DelegateToolLoopUi(onAppend, onToolCall, onComplete, onErr));
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    BeginInvoke((MethodInvoker)delegate
                    { FinalizeToolSend(ctx, renderTimer, sbLock, segs, msg); });
                }
            });
        }

        // Finalize a tool turn on the UI thread. error == null means success. The orchestrator has
        // already appended the structured assistant/tool messages to history, so we only flush the
        // transcript bubbles and save.
        private void FinalizeToolSend(TabManager.ChatTabContext ctx, System.Windows.Forms.Timer renderTimer,
                                      object sbLock, List<LiveSeg> segs, string error)
        {
            try { renderTimer.Stop(); renderTimer.Dispose(); }
            catch { }
            try { ctx.Transcript.StickToBottomDuringStreaming = false; }
            catch { }

            // Flush any pending segment content (final delta the timer may not have rendered yet).
            MaterializeLiveSegs(ctx, sbLock, segs);

            // A failed turn surfaces as a chrome-less red notice below whatever (if anything) the model
            // managed to produce — never baked into a bubble.
            if (error != null)
                ShowTranscriptError(ctx.Transcript, error);

            if (error == null)
            {
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation);
            }
            ctx.IsSending = false;
            if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
        }

        // Create/update the transcript messages backing the ordered live segments. Each segment becomes
        // a real message only once it has content, so empty placeholder bubbles never appear. Runs on
        // the UI thread (render timer + finalize). Segment text is snapshotted under sbLock; the
        // transcript index/last-rendered length live on the segment and are only touched here.
        private void MaterializeLiveSegs(TabManager.ChatTabContext ctx, object sbLock, List<LiveSeg> segs)
        {
            LiveSeg[] snap; string[] texts;
            lock (sbLock)
            {
                snap = segs.ToArray();
                texts = new string[snap.Length];
                for (int i = 0; i < snap.Length; i++) texts[i] = snap[i].Text.ToString();
            }
            for (int i = 0; i < snap.Length; i++)
            {
                LiveSeg seg = snap[i];
                string text = texts[i];
                if (text.Length == 0) continue; // not materialized until it actually has content
                if (seg.Index < 0)
                {
                    seg.Index = ctx.Transcript.AddMessageGetIndex(seg.Role, text);
                    seg.LastLen = text.Length;
                }
                else if (text.Length != seg.LastLen)
                {
                    ctx.Transcript.UpdateMessageAt(seg.Index, text);
                    seg.LastLen = text.Length;
                }
            }
        }

        // Build a list of messages for the model, appending any attachments to the user message content
        private static List<ChatMessage> BuildMessagesForModel(List<ChatMessage> history)
        {
            var list = new List<ChatMessage>();
            if (history == null) return list;
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m == null) continue;
                string content = m.Content ?? string.Empty;
                if (m.Attachments != null && m.Attachments.Count > 0)
                {
                    var sb = new StringBuilder();
                    sb.Append(content);
                    for (int j = 0; j < m.Attachments.Count; j++)
                    {
                        var af = m.Attachments[j];
                        if (af == null) continue;
                        sb.AppendLine();
                        sb.AppendLine("--- Attached File: " + af.FileName + " ---");
                        sb.AppendLine(af.Content ?? string.Empty);
                        sb.AppendLine("--- End Attached File: " + af.FileName + " ---");
                    }
                    content = sb.ToString();
                }
                var nm = new ChatMessage(m.Role, content);
                // Preserve tool-call linkage so this is safe as the orchestrator's request transform.
                nm.ToolCalls = m.ToolCalls;
                nm.ToolCallId = m.ToolCallId;
                list.Add(nm);
            }
            return list;
        }

        private void PopulateModelsFromSettings()
        {
            try
            {
                var list = AppSettings.GetList("models");
                string def = AppSettings.GetString("default_model");

                if (list != null && list.Count > 0 && this.cmbModel != null)
                {
                    // Preserve the active tab's chosen model if possible
                    string currentTabModel = null;
                    try
                    {
                        var act = _tabManager != null ? _tabManager.GetActiveContext() : null;
                        if (act != null) currentTabModel = act.SelectedModel;
                    }
                    catch { }

                    this.cmbModel.BeginUpdate();
                    try
                    {
                        this.cmbModel.Items.Clear();
                        foreach (var m in list) this.cmbModel.Items.Add(m);

                        string target = !string.IsNullOrEmpty(currentTabModel) ? currentTabModel : (!string.IsNullOrEmpty(def) ? def : (list.Count > 0 ? list[0] : null));
                        if (!string.IsNullOrEmpty(target))
                        {
                            _syncingModelCombo = true;
                            try { this.cmbModel.Text = target; }
                            finally { _syncingModelCombo = false; }
                        }
                    }
                    finally
                    {
                        this.cmbModel.EndUpdate();
                    }
                    // Ensure dropdown width fits longest item (or control width if shorter)
                    try { AdjustComboDropDownWidth(this.cmbModel); }
                    catch { }
                    // Ensure the active context stores whatever is shown
                    var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                    if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                }
            }
            catch { }
        }

        // Ensure the combo's dropdown width accommodates the widest item text
        private void AdjustComboDropDownWidth(ComboBox combo)
        {
            if (combo == null) return;
            try
            {
                int maxWidth = combo.Width; // at least the control width
                // Measure each item text
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string s = combo.GetItemText(combo.Items[i]) ?? string.Empty;
                    if (s.Length == 0) continue;
                    // TextRenderer accounts for ComboBox text rendering in WinForms
                    int w = TextRenderer.MeasureText(s, combo.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine).Width;
                    if (w > maxWidth) maxWidth = w;
                }
                // Add space for vertical scrollbar if it will appear, plus a small margin
                int extra = 10;
                try { if (combo.Items.Count > combo.MaxDropDownItems) extra += SystemInformation.VerticalScrollBarWidth; }
                catch { }
                int target = Math.Max(combo.Width, Math.Min(maxWidth + extra, 2000)); // reasonable upper bound
                combo.DropDownWidth = target;
            }
            catch { }
        }

        // Recompute dropdown width right before it opens
        private void cmbModel_DropDownAdjustWidth(object sender, EventArgs e)
        {
            try { AdjustComboDropDownWidth(this.cmbModel); }
            catch { }
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
        }

        // File > New Conversation
        private void miNewConversation_Click(object sender, EventArgs e)
        {
            if (_tabManager != null) _tabManager.CreateConversationTab();
        }

        // File > Close Conversation
        private void closeConversationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tabManager != null) _tabManager.CloseActiveConversationTab();
        }

        // Center the label and link vertically within the banner panel
        private void LayoutApiKeyBanner()
        {
            try
            {
                if (this.pnlApiKeyBanner == null) return;
                int h = this.pnlApiKeyBanner.ClientSize.Height;
                if (this.lblNoApiKey != null)
                {
                    int y = Math.Max(0, (h - this.lblNoApiKey.Height) / 2);
                    this.lblNoApiKey.Top = y;
                }
                if (this.lnkOpenSettings != null)
                {
                    int y = Math.Max(0, (h - this.lnkOpenSettings.Height) / 2);
                    this.lnkOpenSettings.Top = y;
                }
            }
            catch { }
        }

        private void UpdateApiKeyBanner()
        {
            try
            {
                string key = AppSettings.GetString("openrouter_api_key");
                bool hasKey = (key != null && key.Trim().Length > 0);
                if (this.pnlApiKeyBanner != null)
                    this.pnlApiKeyBanner.Visible = !hasKey;

                // Enable/disable input controls based on configuration
                if (this.txtMessage != null) this.txtMessage.Enabled = hasKey;
                if (this.btnSend != null) this.btnSend.Enabled = hasKey;
                if (this.cmbModel != null) this.cmbModel.Enabled = hasKey;
                if (this.btnAttach != null) this.btnAttach.Enabled = hasKey;

                // Keep banner layout tidy when toggled
                if (!hasKey) LayoutApiKeyBanner();

                // Recompute input height because available space changes when the banner toggles
                if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
            }
            catch
            {
                if (this.pnlApiKeyBanner != null) this.pnlApiKeyBanner.Visible = true;
                if (this.txtMessage != null) this.txtMessage.Enabled = false;
                if (this.btnSend != null) this.btnSend.Enabled = false;
                if (this.cmbModel != null) this.cmbModel.Enabled = false;
                if (this.btnAttach != null) this.btnAttach.Enabled = false;

                // Still try to recalc layout
                try { if (_inputManager != null) _inputManager.AdjustInputBoxHeight(); }
                catch { }
            }
        }

        private void lnkOpenSettings_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                DialogResult dr = dlg.ShowDialog(this);
                // The dialog writes settings.json directly; drop the cached copy so reads are fresh.
                AppSettings.Reload();
                if (dr == DialogResult.OK)
                {
                    InitializeClient();
                }
                else
                {
                    // They might have clicked Apply
                    InitializeClient();
                }
                UpdateApiKeyBanner();
                try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
                catch { }
            }
        }

        // ===== Attachments =====
        private void btnAttach_Click(object sender, EventArgs e)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;
            EnsureAttachmentService();
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Attach File(s)";
                ofd.Multiselect = true;
                ofd.Filter = (_attachmentService != null) ? _attachmentService.BuildOpenFileDialogFilter() : "All Files|*.*";
                ofd.CheckFileExists = true;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;
                List<string> skipped = new List<string>();
                var extracted = (_attachmentService != null) ? _attachmentService.ExtractMany(ofd.FileNames, out skipped) : new List<AttachedFile>();
                if (extracted != null && extracted.Count > 0)
                {
                    if (ctx.PendingAttachments == null) ctx.PendingAttachments = new List<AttachedFile>();
                    ctx.PendingAttachments.AddRange(extracted);
                }
                if (skipped != null && skipped.Count > 0)
                {
                    try
                    {
                        MessageBox.Show(this,
                            "Skipped unsupported items:\n - " + string.Join("\n - ", skipped.ToArray()),
                            "Attach Files",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }
                    catch { }
                }
                RebuildAttachmentsBanner();
            }
        }

        // Replace NULs and non-printable control chars (except CR/LF/TAB) and normalize newlines.
        private static string SanitizeDisplayText(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

            bool needs = false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\')
                {
                    if (i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                    { needs = true; break; }
                }
                if (c == '\0' || (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t'))
                { needs = true; break; }
            }
            if (!needs)
            {
                return text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            }

            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\\' && i + 5 < text.Length && text[i + 1] == 'u' && text[i + 2] == '0' && text[i + 3] == '0' && text[i + 4] == '0' && text[i + 5] == '0')
                {
                    sb.Append(' ');
                    i += 5;
                    continue;
                }
                if (c == '\0') { sb.Append(' '); continue; }
                if (char.IsControl(c) && c != '\r' && c != '\n' && c != '\t') { sb.Append(' '); continue; }
                if (char.IsSurrogate(c))
                {
                    if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                    {
                        sb.Append(c);
                        sb.Append(text[i + 1]);
                        i++;
                        continue;
                    }
                    sb.Append(' ');
                    continue;
                }
                sb.Append(c);
            }

            string cleaned = sb.ToString();
            cleaned = cleaned.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
            return cleaned;
        }

        private void EnsureAttachmentService()
        {
            if (_attachmentService == null)
            {
                // Inject syntax-highlighter file extensions into the attachment service
                string[] patterns = null;
                try { patterns = SyntaxHighlighter.GetAllHighlighterFilePatterns(); }
                catch { patterns = null; }
                if (patterns != null && patterns.Length > 0)
                    _attachmentService = new AttachmentService(patterns);
                else
                    _attachmentService = new AttachmentService();
            }
        }

        private void RebuildAttachmentsBanner()
        {
            try
            {
                if (this.pnlAttachmentsBanner == null) return;
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                this.pnlAttachmentsBanner.SuspendLayout();
                try
                {
                    this.pnlAttachmentsBanner.Controls.Clear();
                    if (ctx == null || ctx.PendingAttachments == null || ctx.PendingAttachments.Count == 0)
                    {
                        this.pnlAttachmentsBanner.Visible = false;
                        // collapse height when hidden
                        this.pnlAttachmentsBanner.Height = 0;
                    }
                    else
                    {
                        this.pnlAttachmentsBanner.Visible = true;
                        for (int i = 0; i < ctx.PendingAttachments.Count; i++)
                        {
                            var af = ctx.PendingAttachments[i];
                            if (af == null) continue;
                            var chip = CreateAttachmentChip(af, ctx.PendingAttachments);
                            this.pnlAttachmentsBanner.Controls.Add(chip);
                        }
                        // Ensure the banner grows to fit wrapped rows
                        int availableWidth = this.pnlAttachmentsBanner.Parent != null ? this.pnlAttachmentsBanner.Parent.ClientSize.Width : this.pnlAttachmentsBanner.Width;
                        if (availableWidth <= 0) availableWidth = this.ClientSize.Width;
                        var pref = this.pnlAttachmentsBanner.GetPreferredSize(new Size(availableWidth, 0));
                        // include padding
                        int targetHeight = Math.Max(0, pref.Height);
                        this.pnlAttachmentsBanner.Height = targetHeight;
                    }
                }
                finally
                {
                    this.pnlAttachmentsBanner.ResumeLayout();
                }
                if (_inputManager != null) _inputManager.AdjustInputBoxHeight();
            }
            catch { }
        }

        private Control CreateAttachmentChip(AttachedFile afRef, IList<AttachedFile> list)
        {
            var panel = new Panel();
            panel.Margin = new Padding(4, 2, 4, 2);
            panel.Padding = new Padding(10, 4, 10, 4); // slightly smaller chips
            panel.AutoSize = false; // we'll size explicitly based on text
            panel.BackColor = Color.Transparent; // we'll paint background ourselves
            panel.Cursor = Cursors.Hand;

            string text = afRef != null ? (afRef.FileName ?? string.Empty) : string.Empty;
            panel.Font = (this.txtMessage != null ? this.txtMessage.Font : this.Font);

            // Measure and set size so FlowLayout can lay out correctly
            Size textSize = TextRenderer.MeasureText(text, panel.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.SingleLine);
            int width = Math.Max(18, textSize.Width + panel.Padding.Horizontal);
            int height = Math.Max(18, textSize.Height + panel.Padding.Vertical);
            panel.Size = new Size(width, height);

            int cornerRadius = 6; // reduce radius for a sharper pill
            Color baseFill = Color.FromArgb(230, 230, 230);
            Color hoverFill = Color.FromArgb(240, 240, 240);
            Color pressFill = Color.FromArgb(210, 210, 210);
            Color fillColor = baseFill;
            bool hover = false, pressed = false; // track hover for text color

            // Rounded look via Paint, clip region, and centered text
            panel.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                Rectangle r = new Rectangle(0, 0, Math.Max(1, panel.Width - 1), Math.Max(1, panel.Height - 1));
                using (var path = ChatTranscriptControl_RoundedRect(r, cornerRadius))
                using (var b = new SolidBrush(fillColor))
                using (var pen = new Pen(Color.FromArgb(200, 200, 200)))
                {
                    g.FillPath(b, path);
                    g.DrawPath(pen, path);
                }
                var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine;
                var textColor = hover ? Color.Red : panel.ForeColor;
                TextRenderer.DrawText(e.Graphics, text, panel.Font, r, textColor, flags);
            };

            EventHandler applyRegion = (s, e) =>
            {
                try
                {
                    Rectangle r = new Rectangle(0, 0, Math.Max(1, panel.Width - 1), Math.Max(1, panel.Height - 1));
                    using (var path = ChatTranscriptControl_RoundedRect(r, cornerRadius))
                    { panel.Region = new Region(path); }
                }
                catch { }
            };
            panel.SizeChanged += applyRegion;
            panel.HandleCreated += (s, e) => applyRegion(s, e);

            EventHandler repaint = (s, e) => panel.Invalidate();
            MouseEventHandler onMouseDown = (s, e) => { if (e.Button == MouseButtons.Left) { pressed = true; fillColor = pressFill; repaint(s, e); } };
            EventHandler onMouseEnter = (s, e) => { hover = true; if (!pressed) fillColor = hoverFill; repaint(s, e); };
            EventHandler onMouseLeave = (s, e) => { hover = false; pressed = false; fillColor = baseFill; repaint(s, e); };
            MouseEventHandler onMouseUp = (s, e) =>
            {
                if (pressed && e.Button == MouseButtons.Left)
                {
                    pressed = false; fillColor = hover ? hoverFill : baseFill; repaint(s, e);
                    // Remove this attachment by reference
                    if (afRef != null && list != null)
                    {
                        try { list.Remove(afRef); }
                        catch { }
                        RebuildAttachmentsBanner();
                    }
                }
            };
            panel.MouseEnter += onMouseEnter;
            panel.MouseLeave += onMouseLeave;
            panel.MouseDown += onMouseDown;
            panel.MouseUp += onMouseUp;

            // Double-click opens a preview
            EventHandler onDoubleClick = (s, e) => { if (afRef != null) OpenAttachmentInViewer(afRef); };
            panel.DoubleClick += onDoubleClick;

            return panel;
        }

        private void ClearAttachmentsBanner()
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            try { if (ctx != null && ctx.PendingAttachments != null) ctx.PendingAttachments.Clear(); }
            catch { }
            RebuildAttachmentsBanner();
        }

        private void OpenAttachmentInViewer(AttachedFile af)
        {
            if (af == null) return;
            try
            {
                using (var dlg = new FileViewerForm())
                {
                    // Access RichTextBox via reflection to keep designer file untouched
                    var rtbField = typeof(FileViewerForm).GetField("rtbFileText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var rtb = rtbField != null ? (RichTextBox)rtbField.GetValue(dlg) : null;
                    if (rtb != null)
                    {
                        // Sanitize content for RichEdit: replace NULs (\u0000) which truncate display
                        string content = af.Content ?? string.Empty;
                        if (content.IndexOf('\0') >= 0)
                        {
                            try { Logger.Log("Viewer", "Attachment contains NUL characters; sanitizing for display."); }
                            catch { }
                            content = content.Replace('\0', ' ');
                        }
                        // Normalize newlines to CRLF for consistent caret/selection math
                        content = content.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
                        rtb.Text = content;
                        string lang = GetFileExtension(af.FileName);

                        // Match theme with chat transcript
                        bool dark = false;
                        try
                        {
                            string theme = AppSettings.GetString("theme");
                            dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { dark = false; }

                        var colors = ThemeService.GetColors(dark);
                        rtb.BackColor = colors.UiBackground;
                        rtb.ForeColor = colors.UiForeground;

                        try { RichTextBoxSyntaxHighlighter.Highlight(rtb, lang, dark); }
                        catch { }
                    }
                    dlg.Text = af.FileName ?? "Attachment";
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.ShowDialog(this);
                }
            }
            catch { }
        }

        private string GetFileExtension(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return null;
                string ext = System.IO.Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) return null;
                return ext.TrimStart('.').ToLowerInvariant();
            }
            catch { }
            return null;
        }

        private static System.Drawing.Drawing2D.GraphicsPath ChatTranscriptControl_RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        // Return default model from settings or fallback
        internal string GetConfiguredDefaultModel()
        {
            try
            {
                var def = AppSettings.GetString("default_model");
                if (!string.IsNullOrEmpty(def)) return def;
                var list = AppSettings.GetList("models");
                if (list != null && list.Count > 0) return list[0];
            }
            catch { }
            return "openai/gpt-4o";
        }

        // Sync the model combo box to the active tab's SelectedModel
        internal void SyncComboModelFromActiveTab()
        {
            try
            {
                if (this.cmbModel == null) return;
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx == null) return;
                var target = ctx.SelectedModel;
                if (string.IsNullOrEmpty(target)) target = GetConfiguredDefaultModel();
                if (!string.Equals(this.cmbModel.Text, target, StringComparison.Ordinal))
                {
                    _syncingModelCombo = true;
                    try { this.cmbModel.Text = target; }
                    finally { _syncingModelCombo = false; }
                }
            }
            catch { }
        }

        // Update the active tab's SelectedModel when user changes selection in the combo
        private void cmbModel_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_syncingModelCombo) return;
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    // Optional: persist model change only when messages exist and saving is allowed
                    if (ctx.Conversation.History.Count > 0 && !ctx.NoSaveUntilUserSend)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                    }
                }
            }
            catch { }
        }

        // Track typed text into the combo box as model selection per tab
        private void cmbModel_TextUpdate(object sender, EventArgs e)
        {
            if (_syncingModelCombo) return;
            try
            {
                var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    if (ctx.Conversation.History.Count > 0 && !ctx.NoSaveUntilUserSend)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                    }
                }
            }
            catch { }
        }

        // Rebuild a tab's transcript from a conversation's history without blocking the UI:
        // Markdown is parsed on a background thread and the resulting blocks are appended to the
        // transcript in small batches on a UI timer. System messages are skipped; help templates
        // (NoSaveUntilUserSend) are scrolled to the top, otherwise the view sticks to the bottom.
        // Role marker for the chrome-less tool-activity blocks used on reload.
        private const string ToolActivityRole = "toolactivity";

        // Activity marker for a tool call. If the tool maps to a collapsible/labelled record, register
        // it with the transcript under a stable key and return its sentinel; otherwise (or on any
        // failure) return the generic "using <tool>" marker.
        private static string EditDiffMarkerOrCall(ChatTranscriptControl transcript, string name, string argsJson, string key)
        {
            if (transcript != null && !string.IsNullOrEmpty(key))
            {
                try
                {
                    var args = Newtonsoft.Json.Linq.JObject.Parse(string.IsNullOrEmpty(argsJson) ? "{}" : argsJson);
                    string header, body, language; int added, removed;
                    if (TryBuildToolRecord(name, args, out header, out body, out language, out added, out removed))
                    {
                        transcript.RegisterToolRecord(key, header, body, language, added, removed);
                        return McpMarkers.EditDiff(key);
                    }
                }
                catch { /* fall through to the generic marker */ }
            }
            return McpMarkers.Call(name);
        }

        // Surface a streaming/transport failure as a chrome-less red notice in the transcript, so a
        // failed turn (e.g. an unavailable model, or a Claude request that returns nothing) is visible
        // instead of leaving the user staring at an empty/missing bubble.
        private static void ShowTranscriptError(ChatTranscriptControl transcript, string rawError)
        {
            if (transcript == null) return;
            transcript.AddMessage(MessageRole.Tool, MarkdownParser.ErrorSentinel(FriendlyStreamError(rawError)));
        }

        // Light humanization of the raw streamer error for display. Provider messages from OpenRouter
        // are usually already readable, so we mostly pass them through under a clear prefix.
        private static string FriendlyStreamError(string raw)
        {
            string e = raw == null ? string.Empty : raw.Trim();
            if (e.Length == 0)
                return "The request failed before the model responded. Please try again or pick a different model.";
            return "The request failed: " + e;
        }

        // Maps a tool call to a transcript record. An empty body yields a one-line label (no expansion);
        // a non-empty body is a collapsible record highlighted in 'language'. Returns false for tools
        // that should keep the generic marker.
        private static bool TryBuildToolRecord(string name, Newtonsoft.Json.Linq.JObject args, out string header, out string body, out string language, out int added, out int removed)
        {
            header = null; body = string.Empty; language = "text"; added = -1; removed = -1;
            switch (name)
            {
                case "files__edit":
                {
                    string path = Str(args, "path");
                    LineDiffResult diff = DiffUtil.BuildLineDiff(Str(args, "old_string"), Str(args, "new_string"));
                    // The +N/-N counts ride alongside (color-coded at draw time), so the header label
                    // is just the path.
                    header = "edited " + (path.Length > 0 ? path : "(file)");
                    body = diff.Body; language = "diff"; added = diff.Added; removed = diff.Removed; return true;
                }
                case "files__write":
                {
                    string path = Str(args, "path");
                    header = "Wrote " + (path.Length > 0 ? path : "(file)");
                    body = Str(args, "content");
                    language = SyntaxHighlighter.GetLanguageForFileName(path) ?? "text";
                    return true;
                }
                case "files__delete":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Deleted " + path; return true;
                }
                case "files__read":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Read " + path + LineRangeSuffix(args); return true;
                }
                case "files__list":
                {
                    string path = Str(args, "path"); if (path.Length == 0) return false;
                    header = "Listed " + path + (Bool(args, "recursive") ? " (recursive)" : ""); return true;
                }
                case "files__search":
                {
                    string query = Str(args, "query"); if (query.Length == 0) return false;
                    header = "Searched files"; body = query;
                    language = Bool(args, "regex") ? "regex" : "text"; return true;
                }
                case "command__run":
                {
                    string cmd = Str(args, "command"); if (cmd.Trim().Length == 0) return false;
                    header = "Ran a command"; body = cmd; language = "batch"; return true;
                }
                case "web__search":
                {
                    string query = Str(args, "query"); if (query.Trim().Length == 0) return false;
                    header = "Searched the web"; body = query; return true;
                }
                case "web__extract":
                {
                    string urls = JoinUrls(args); if (urls.Length == 0) return false;
                    int n = urls.Split('\n').Length;
                    header = n > 1 ? ("Read " + n + " web pages") : "Read a web page";
                    body = urls; return true;
                }
                case "git__commit":
                {
                    string msg = Str(args, "message"); if (msg.Trim().Length == 0) return false;
                    header = "Committed"; body = msg; return true;
                }
                case "git__push":
                {
                    string remote = Str(args, "remote"); string branch = Str(args, "branch");
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : branch;
                    header = tgt.Length > 0 ? ("Pushed to " + tgt) : "Pushed commits"; return true;
                }
                case "git__status": header = "Checked git status"; return true;
                case "git__log": header = "Viewed git log"; return true;
                case "git__diff":
                {
                    string path = Str(args, "path");
                    header = "Viewed git diff" + (Bool(args, "staged") ? " (staged)" : "") + (path.Length > 0 ? " of " + path : "");
                    return true;
                }
                case "reveal_tools": header = "Checking available tools"; return true;
                default: return false;
            }
        }

        private static string Str(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try { var t = args[name]; return t == null ? string.Empty : ((string)t ?? string.Empty); }
            catch { return string.Empty; }
        }
        private static bool Bool(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try { var t = args[name]; return t != null && (bool)t; }
            catch { return false; }
        }
        private static string LineRangeSuffix(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var s = args["start_line"]; var e = args["end_line"];
                if (s != null && e != null) return " (lines " + (int)s + "–" + (int)e + ")";
                if (s != null) return " (from line " + (int)s + ")";
                if (e != null) return " (through line " + (int)e + ")";
            }
            catch { }
            return string.Empty;
        }
        private static string JoinUrls(Newtonsoft.Json.Linq.JObject args)
        {
            try
            {
                var arr = args["urls"] as Newtonsoft.Json.Linq.JArray;
                if (arr == null) return string.Empty;
                var sb = new System.Text.StringBuilder();
                foreach (var u in arr)
                {
                    string s = (string)u; if (string.IsNullOrEmpty(s)) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(s);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        private void RebuildTranscriptAsync(TabManager.ChatTabContext ctx, Conversation convo)
        {
            if (ctx == null || ctx.Transcript == null || convo == null) return;
            try
            {
                ctx.Transcript.ClearMessages();
                if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                try { ctx.Transcript.RefreshTheme(); }
                catch { }

                // Snapshot messages to process. System and tool-result messages are not shown. A
                // tool-using assistant turn is persisted as several messages (assistant-with-tool_calls,
                // tool result(s), ..., final assistant). Emit each assistant message's text as its own
                // bubble and its tool calls as a chrome-less "using <tool>" block, interleaved in
                // history order — so multi-turn tool use reads chronologically (text, tools, text, ...),
                // matching the live streamed view. Tool results are never shown (the model summarizes).
                var items = new List<ChatMessage>();
                // Consecutive tool-call runs that aren't separated by real assistant text merge into one
                // chrome-less block (e.g. reveal_tools followed by the call it enabled), so they read as
                // tightly as a single multi-call turn instead of two blocks with a gap between them. A
                // real assistant text bubble or a user message flushes the running block.
                System.Text.StringBuilder pendingTools = null;
                Action flushTools = delegate
                {
                    if (pendingTools != null && pendingTools.Length > 0)
                        items.Add(new ChatMessage(ToolActivityRole, pendingTools.ToString()));
                    pendingTools = null;
                };
                foreach (var m in convo.History)
                {
                    if (m == null) continue;
                    string role = (m.Role ?? string.Empty).ToLowerInvariant();
                    if (role == "system" || role == "tool") continue; // not shown

                    if (role == "user")
                    {
                        flushTools();
                        items.Add(m);
                        continue;
                    }

                    // assistant text (intermediate or final) -> its own bubble, closing any tool run
                    if (!string.IsNullOrEmpty(m.Content) && m.Content.Trim().Length > 0)
                    {
                        flushTools();
                        items.Add(new ChatMessage("assistant", m.Content));
                    }

                    // this turn's tool calls -> append to the running chrome-less block
                    if (m.ToolCalls != null && m.ToolCalls.Count > 0)
                    {
                        if (pendingTools == null) pendingTools = new System.Text.StringBuilder();
                        for (int ti = 0; ti < m.ToolCalls.Count; ti++)
                        {
                            var tc = m.ToolCalls[ti];
                            if (tc == null) continue;
                            if (pendingTools.Length > 0) pendingTools.Append("\r\n");
                            string key = !string.IsNullOrEmpty(tc.Id) ? tc.Id : ("edit" + ti);
                            pendingTools.Append(EditDiffMarkerOrCall(ctx.Transcript, tc.Name, tc.ArgumentsJson, key));
                        }
                    }
                }
                flushTools();

                // Producer-consumer: parse off-UI, consume on UI timer in small chunks
                var parsed = new List<ParsedMessage>(Math.Max(4, items.Count));
                int produced = 0; // parsed items ready in 'parsed'
                int consumed = 0; // items appended to transcript
                bool parsingDone = false;
                object gate = new object();

                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        const int parseChunk = 40; // parse up to N per pass
                        int i = 0;
                        while (i < items.Count)
                        {
                            int count = Math.Min(parseChunk, items.Count - i);
                            var local = new ParsedMessage[count];
                            for (int k = 0; k < count; k++)
                            {
                                var m = items[i + k];
                                MessageRole role;
                                if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase)) role = MessageRole.Assistant;
                                else if (string.Equals(m.Role, ToolActivityRole, StringComparison.OrdinalIgnoreCase)) role = MessageRole.Tool;
                                else role = MessageRole.User;
                                var text = m.Content ?? string.Empty;
                                var blocks = MarkdownParser.ParseMarkdown(text);
                                var att = (m.Attachments != null && m.Attachments.Count > 0) ? new List<AttachedFile>(m.Attachments) : null;
                                local[k] = new ParsedMessage { Role = role, Text = text, Blocks = blocks, Attachments = att };
                            }
                            lock (gate)
                            {
                                parsed.AddRange(local);
                                produced += count;
                            }
                            i += count;
                        }
                    }
                    catch { }
                    finally { parsingDone = true; }
                });

                // UI timer consumes progressively
                var timer = new System.Windows.Forms.Timer();
                timer.Interval = 15; // ~60 fps budget for small bursts
                timer.Tick += (s2, e2) =>
                {
                    int avail;
                    lock (gate) { avail = produced - consumed; }
                    if (avail <= 0)
                    {
                        if (parsingDone) { timer.Stop(); timer.Dispose(); }
                        return;
                    }

                    // Consume a small batch to keep the UI fluid
                    int take = Math.Min(20, avail);
                    ctx.Transcript.BeginBatchUpdates();
                    try
                    {
                        for (int k = 0; k < take; k++)
                        {
                            ParsedMessage pm;
                            lock (gate) { pm = parsed[consumed]; consumed++; }
                            if (pm != null)
                                ctx.Transcript.AddParsedMessage(pm.Role, pm.Text, pm.Blocks, pm.Attachments);
                        }
                    }
                    finally
                    {
                        bool last = parsingDone && consumed >= items.Count;
                        bool scrollToBottom = last && !ctx.NoSaveUntilUserSend;
                        ctx.Transcript.EndBatchUpdates(scrollToBottom);
                        if (last)
                        {
                            // Help templates (no-save until user sends) should land at the top
                            try { if (ctx.NoSaveUntilUserSend && ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                            catch { }
                            timer.Stop();
                            timer.Dispose();
                        }
                    }
                };
                timer.Start();
            }
            catch { }
        }

        private void OpenConversationInNewTab(Conversation convo)
        {
            if (this.tabControl1 == null || convo == null) return;
            var ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            if (ctx == null) return;

            // Replace the conversation and refresh transcript UI
            ctx.Conversation = convo;
            ctx.SelectedModel = string.IsNullOrEmpty(convo.SelectedModel) ? GetSelectedModel() : convo.SelectedModel;
            try { this.cmbModel.Text = ctx.SelectedModel; }
            catch { }
            ConversationStore.EnsureConversationId(ctx.Conversation);
            if (_sidebarManager != null && ctx.Conversation != null)
                _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);

            // Rebuild transcript UI off the UI thread to avoid freezes on large histories. During
            // session restore this is deferred (see HydrateTabIfNeeded) so only the visible tab renders.
            if (_restoringTabs) ctx.NeedsTranscriptRebuild = true;
            else RebuildTranscriptAsync(ctx, convo);

            // Adopt the conversation's saved working folder.
            ApplyLoadedWorkingDir(ctx);

            // Update tab title and window
            try
            {
                ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "Conversation" : convo.Name;
                UpdateWindowTitleFromActiveTab();
            }
            catch { }

            // Focus input when a new tab opens from history
            if (_inputManager != null) _inputManager.FocusInputSoon();
        }

        // Load a conversation into a specific tab context (typically blank) and select it
        private void OpenConversationInTab(TabManager.ChatTabContext ctx, Conversation convo)
        {
            if (this.tabControl1 == null || convo == null || ctx == null) return;

            try
            {
                // Update model selection for this tab
                ctx.Conversation = convo;
                ctx.SelectedModel = string.IsNullOrEmpty(convo.SelectedModel) ? GetSelectedModel() : convo.SelectedModel;
                try { this.cmbModel.Text = ctx.SelectedModel; }
                catch { }

                // Update sidebar tracking to point this page at the loaded conversation id
                try
                {
                    // Remove any prior mapping for this page, then track the new id
                    UntrackOpenConversation(ctx.Page);
                }
                catch { }
                ConversationStore.EnsureConversationId(ctx.Conversation);
                if (_sidebarManager != null && ctx.Conversation != null)
                    _sidebarManager.TrackOpenConversation(ctx.Conversation.Id, ctx.Page);

                // Rebuild transcript UI off the UI thread to avoid freezes on large histories. During
                // session restore this is deferred (see HydrateTabIfNeeded) so only the visible tab
                // renders up front; the rest build when first selected.
                if (_restoringTabs) ctx.NeedsTranscriptRebuild = true;
                else RebuildTranscriptAsync(ctx, convo);

                // Adopt the conversation's saved working folder.
                ApplyLoadedWorkingDir(ctx);

                // Hook name updates for this conversation
                try
                {
                    ctx.Conversation.NameGenerated += delegate(string name)
                    {
                        try
                        {
                            if (IsHandleCreated)
                            {
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                                    UpdateWindowTitleFromActiveTab();
                                });
                            }
                        }
                        catch { }
                    };
                }
                catch { }

                // Update tab title and window
                try
                {
                    ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "Conversation" : convo.Name;
                    // Select this tab and update window title
                    SelectTab(ctx.Page);
                    UpdateWindowTitleFromActiveTab();
                }
                catch { }

                if (_inputManager != null) _inputManager.FocusInputSoon();
                if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
            }
            catch { }
        }

        // Find a blank tab (no messages). Prefer the active one, else any.
        private TabManager.ChatTabContext FindBlankTabPreferActive()
        {
            try
            {
                if (_tabManager == null) return null;
                var active = _tabManager.GetActiveContext();
                if (IsBlank(active)) return active;
                foreach (var kv in _tabManager.TabContexts)
                {
                    var ctx = kv.Value;
                    if (IsBlank(ctx)) return ctx;
                }
            }
            catch { }
            return null;
        }

        private static bool IsBlank(TabManager.ChatTabContext ctx)
        {
            try
            {
                return ctx != null && ctx.Conversation != null &&
                       (ctx.Conversation.History == null || ctx.Conversation.History.Count == 0);
            }
            catch { return false; }
        }

        private void miApiKeysHelp_Click(object sender, EventArgs e)
        {
            // If an API Keys help tab is already open, focus it
            try
            {
                if (this.tabControl1 != null && _tabManager != null)
                {
                    foreach (TabPage p in this.tabControl1.TabPages)
                    {
                        var ctxOpen = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                        if (ctxOpen != null && ctxOpen.Conversation != null && string.Equals(ctxOpen.Conversation.Id, HelpApiKeysId, StringComparison.Ordinal))
                        {
                            SelectTab(p);
                            UpdateWindowTitleFromActiveTab();
                            try { if (ctxOpen.Transcript != null) ctxOpen.Transcript.ScrollToTop(); }
                            catch { }
                            return;
                        }
                    }
                }
            }
            catch { }
            // Reuse any blank tab if available (prefer active); otherwise create a new one
            TabManager.ChatTabContext ctx = FindBlankTabPreferActive();
            if (ctx == null)
            {
                ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (ctx == null) return;
            try { SelectTab(ctx.Page); }
            catch { }

            try
            {
                // Load base help conversation from embedded resource
                Conversation convo = null;
                try
                {
                    var asm = typeof(MainForm).Assembly;
                    // Resource name follows default: RootNamespace.folder.filename
                    using (var s = asm.GetManifestResourceStream("GxPT.Resources.Help.help_api_keys.json"))
                    {
                        if (s != null)
                        {
                            using (var sr = new StreamReader(s, Encoding.UTF8, true))
                            {
                                string json = sr.ReadToEnd();
                                convo = ConversationStore.LoadFromJson(_client, json);
                            }
                        }
                    }
                }
                catch { }
                if (convo != null)
                {
                    // Always treat help templates as no-save until user sends a new message
                    ctx.NoSaveUntilUserSend = true;
                    // Keep the specialized help id from the template for restoration
                    // Give the tab its name from the conversation
                    try { ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "API Keys Help" : convo.Name; }
                    catch { }
                    OpenConversationInTab(ctx, convo);
                    // Ensure help opens scrolled to the top
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                }
                else
                {
                    // No template found: show a notice and do not inject any hardcoded messages
                    MessageBox.Show(this,
                        "Help template not found in embedded resources.",
                        "API Keys Help",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                UpdateWindowTitleFromActiveTab();
                if (_inputManager != null) _inputManager.FocusInputSoon();
            }
            catch { }
        }

        private void miPrivacyHelp_Click(object sender, EventArgs e)
        {
            // If a Privacy help tab is already open, focus it
            try
            {
                if (this.tabControl1 != null && _tabManager != null)
                {
                    foreach (TabPage p in this.tabControl1.TabPages)
                    {
                        var ctxOpen = _tabManager.TabContexts.ContainsKey(p) ? _tabManager.TabContexts[p] : null;
                        if (ctxOpen != null && ctxOpen.Conversation != null && string.Equals(ctxOpen.Conversation.Id, HelpPrivacyId, StringComparison.Ordinal))
                        {
                            SelectTab(p);
                            UpdateWindowTitleFromActiveTab();
                            try { if (ctxOpen.Transcript != null) ctxOpen.Transcript.ScrollToTop(); }
                            catch { }
                            return;
                        }
                    }
                }
            }
            catch { }
            // Reuse any blank tab if available (prefer active); otherwise create a new one
            TabManager.ChatTabContext ctx = FindBlankTabPreferActive();
            if (ctx == null)
            {
                ctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (ctx == null) return;
            try { SelectTab(ctx.Page); }
            catch { }

            try
            {
                Conversation convo = null;
                try
                {
                    var asm = typeof(MainForm).Assembly;
                    using (var s = asm.GetManifestResourceStream("GxPT.Resources.Help.help_privacy.json"))
                    {
                        if (s != null)
                        {
                            using (var sr = new StreamReader(s, Encoding.UTF8, true))
                            {
                                string json = sr.ReadToEnd();
                                convo = ConversationStore.LoadFromJson(_client, json);
                            }
                        }
                    }
                }
                catch { }
                if (convo != null)
                {
                    ctx.NoSaveUntilUserSend = true;
                    try { ctx.Page.Text = string.IsNullOrEmpty(convo.Name) ? "Privacy" : convo.Name; }
                    catch { }
                    OpenConversationInTab(ctx, convo);
                    // Ensure help opens scrolled to the top
                    try { if (ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                    catch { }
                }
                else
                {
                    MessageBox.Show(this,
                        "Help template not found in embedded resources.",
                        "Privacy",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    return;
                }

                UpdateWindowTitleFromActiveTab();
                if (_inputManager != null) _inputManager.FocusInputSoon();
            }
            catch { }
        }

        private void miAbout_Click(object sender, EventArgs e)
        {
            using (var dlg = new AboutForm())
            {
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.ShowDialog(this);
            }
        }

        // Compare attachments by filename and content (same order and count)
        private static bool AreAttachmentsEqual(List<AttachedFile> a, List<AttachedFile> b)
        {
            try
            {
                int ac = (a != null) ? a.Count : 0;
                int bc = (b != null) ? b.Count : 0;
                if (ac != bc) return false;
                for (int i = 0; i < ac; i++)
                {
                    var ai = a[i]; var bi = b[i];
                    string afn = ai != null ? (ai.FileName ?? string.Empty) : string.Empty;
                    string bfn = bi != null ? (bi.FileName ?? string.Empty) : string.Empty;
                    if (!string.Equals(afn, bfn, StringComparison.Ordinal)) return false;
                    string acnt = ai != null ? (ai.Content ?? string.Empty) : string.Empty;
                    string bcnt = bi != null ? (bi.Content ?? string.Empty) : string.Empty;
                    if (!string.Equals(acnt, bcnt, StringComparison.Ordinal)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        // Missing event handlers expected by designer
        private void miConversationHistory_Click(object sender, EventArgs e)
        {
            if (_sidebarManager != null) _sidebarManager.ToggleSidebar();
        }

        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            if (_inputManager != null) _inputManager.HandleKeyDown(e);
        }

        private void txtMessage_Enter(object sender, EventArgs e)
        {
            _inputManager.RemoveHintText();
            _themeManager.ApplyThemeToTextBox();
        }

        private void txtMessage_Leave(object sender, EventArgs e)
        {
            _inputManager.SetHintText();
        }

        private void miDarkMode_Click(object sender, EventArgs e)
        {
            try
            {
                // Toggle theme string between light and dark
                string theme = AppSettings.GetString("theme") ?? "light";
                bool isDark = theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                bool toDark = !isDark;

                // Persist setting
                AppSettings.SetString("theme", toDark ? "dark" : "light");

                // Update menu checked state immediately
                if (this.miDarkMode != null) this.miDarkMode.Checked = toDark;

                // Apply theme to all transcripts and relevant UI controls now
                if (_themeManager != null)
                {
                    _themeManager.ApplyThemeToAllTranscripts();
                    _themeManager.ApplyFontSizeSettingToAllUi(); // keep fonts consistent; no-op for theme but safe
                }

                // Also refresh tab headers (font/color may rely on system colors)
                try { if (this.tabControl1 != null) this.tabControl1.Invalidate(); }
                catch { }
            }
            catch { }
        }

        private void miDeleteConversations_Click(object sender, EventArgs e)
        {
            try
            {
                var dr = MessageBox.Show(
                    this,
                    "This will permanently delete all saved conversations. This action cannot be undone.\n\nDo you want to continue?",
                    "Delete All Conversations",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                if (dr != DialogResult.Yes) return; // default is No/Cancel

                int deleted = 0;
                try
                {
                    deleted = ConversationStore.DeleteAll();
                }
                catch { }

                // Refresh UI (sidebar list, etc.)
                try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                catch { }

                MessageBox.Show(this,
                    deleted > 0 ? "All conversations have been deleted." : "No conversations were found to delete.",
                    "Delete All Conversations",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch { }
        }

        private void miNextTab_Click(object sender, EventArgs e)
        {
            _tabManager.SelectNextTab();
        }

        private void miPreviousTab_Click(object sender, EventArgs e)
        {
            _tabManager.SelectPreviousTab();
        }
    }
}
