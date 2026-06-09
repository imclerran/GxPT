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
        // Slash-command subsystem (built lazily on first send/keystroke; see EnsureSlashCommands).
        private SlashCommandRegistry _slashRegistry;
        private SlashCommandProcessor _slashProcessor;
        private ISlashCommandContext _slashContext;
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

        // Slim cream notice at the top of the chat area, shown when the shipped recommended model list
        // has changed since the user last acknowledged it. Built programmatically (see
        // InitModelUpdateBanner); persists until the user reviews settings or dismisses it.
        private System.Windows.Forms.Panel _modelUpdateBanner;

        // Helper DTO for background-parsed messages
        private sealed class ParsedMessage
        {
            public MessageRole Role;
            public string Text;
            public List<Block> Blocks;
            public List<AttachedFile> Attachments; // optional
            public bool Zdr; // tiny zero-retention corner tag (user/assistant only)
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

            // Build the "updated recommended models" banner (hidden until Load decides whether to show it).
            InitModelUpdateBanner();

            this.Load += (s, e) =>
            {
                UpdateApiKeyBanner();
                MaybeShowModelUpdateBanner();
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
                        // Help conversations start with the workspace strip hidden until the user
                        // explicitly sets a working folder (right-click tab), which re-shows it.
                        if (convo != null) convo.WorkspaceStripDismissed = true;
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
                if (this.miFile != null)
                {
                    this.miFile.DropDownOpening -= miFile_DropDownOpening;
                    this.miFile.DropDownOpening += miFile_DropDownOpening;
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
                    // Owner-draw shows only the model name (after "author/"); the item value stays the
                    // full "author/model" id, so GetSelectedModel and the request are unchanged.
                    this.cmbModel.DrawItem -= cmbModel_DrawItem;
                    this.cmbModel.DrawItem += cmbModel_DrawItem;
                }
            }
            catch { }

            try
            {
                if (this.chkZdrTab != null)
                {
                    this.chkZdrTab.CheckedChanged -= chkZdrTab_CheckedChanged;
                    this.chkZdrTab.CheckedChanged += chkZdrTab_CheckedChanged;
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
            SyncZdrCheckboxFromActiveTab();
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
            string selectedDir;
            if (!FolderPicker.TrySelectFolder(this, ctx.WorkingDir,
                    "Select a working folder for file, git, and command tools in this conversation.",
                    out selectedDir))
                return;
            ctx.WorkingDir = selectedDir;
            RecentWorkDirs.Add(ctx.WorkingDir);
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

        // Reset the per-tab workspace + ZDR *views* to a blank-slate state when the last tab is closed
        // and reused as a fresh conversation. The recycle path already swaps in a new Conversation (with
        // its ZDR and working-dir state cleared), but the tab-level WorkingDir field, the workspace strip,
        // and the ZDR checkbox are derived views that must be re-synced or they keep the closed tab's
        // state. No PersistWorkingDir here: the fresh conversation's WorkingDir is already null and the
        // blank tab shouldn't be saved.
        internal void ResetRecycledTabWorkspaceState(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            // Working folder: drop the tab's folder and return the strip to its "no folder" state, shown
            // like a brand-new tab (the fresh conversation isn't dismissed).
            ctx.WorkingDir = null;
            if (ctx.WorkspaceStrip != null)
            {
                ctx.WorkspaceStrip.SetWorkingDir(null);
                ctx.WorkspaceStrip.Visible = true;
            }
            SyncMcpWorkingDirFromActiveTab(); // release the closed folder's scoped MCP servers
            // ZDR: the new conversation is unlatched and not per-tab ZDR, so re-sync the checkbox view
            // (it reverts to the global default's checked/disabled state).
            SyncZdrCheckboxFromActiveTab();
        }

        // Opens a fresh conversation tab whose working folder is preset to 'dir'. Mirrors the
        // load flow (create tab -> set working dir -> persist -> adopt onto strip + MCP host),
        // and (via ApplyLoadedWorkingDir) bumps 'dir' to the top of the recent list.
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

        // After a conversation is loaded into a tab, adopt its persisted working folder onto the tab
        // context + strip and (re)bind the MCP host to it.
        private void ApplyLoadedWorkingDir(TabManager.ChatTabContext ctx)
        {
            if (ctx == null) return;
            ctx.WorkingDir = (ctx.Conversation != null) ? ctx.Conversation.WorkingDir : null;
            if (!string.IsNullOrEmpty(ctx.WorkingDir)) RecentWorkDirs.Add(ctx.WorkingDir);
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
                return string.IsNullOrEmpty(m) ? ModelDefaults.DefaultModel : m;
            }
            catch
            {
                return ModelDefaults.DefaultModel;
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
                // MSBuild defaults ON when at least one MSBuild engine is found, OFF when none is, and is
                // force-disabled when none is present regardless of the stored setting — so we never launch
                // an MSBuild server whose tools would be an empty set.
                opts.MsBuildEnabled = MsBuildProbe.IsInstalled() && AppSettings.GetBool("mcp_msbuild_enabled", true);
                // Memory is a feature toggle (off by default); its soft index cap is user-configurable
                // (design sec.3/sec.6). Both the server launch and the system-prompt injection read this one
                // setting, so they can never desync.
                opts.MemoryEnabled = AppSettings.GetBool("mcp_memory_enabled", false);
                int memMaxLines = (int)AppSettings.GetDouble("mcp_memory_max_lines", 40);
                opts.MemoryMaxLines = memMaxLines > 0 ? memMaxLines : 40;
                // Skills server (authoring/execution tools); off by default like the other powerful
                // servers. The read-side skills feature is separate (always on when skills exist).
                opts.SkillsEnabled = AppSettings.GetBool("mcp_skills_enabled", false);
                // Skill roots the server resolves scripts against: bundled (<exe>/skills, shipped with the
                // app) and user-global (%AppData%/GxPT/skills). The project root is derived from
                // GXPT_WORKDIR inside the server. These mirror the read-side roots (SkillInjection).
                opts.SkillsBundledRoot = SkillInjection.BundledRoot(baseDir);
                opts.SkillsUserRoot = SkillInjection.UserRoot();
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
                // Re-sync the per-tab checkbox. Its checked/enabled state now depends only on the
                // conversation's own toggle and latch — the global default seeds new conversations but
                // does not retroactively change one that's already open.
                SyncZdrCheckboxFromActiveTab();
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

        // Build the slash-command registry/processor on first use. Defaults are merged with the user's
        // %AppData%/GxPT/commands.json (if present). The context reads live state through delegates so
        // it always sees the current working folder and the current MCP registry (which is recreated
        // when the working folder changes).
        private void EnsureSlashCommands()
        {
            if (_slashProcessor != null) return;

            string userJson = null;
            try
            {
                string path = Path.Combine(AppSettings.SettingsDirectory, "commands.json");
                if (File.Exists(path)) userJson = File.ReadAllText(path);
            }
            catch { }

            // Built-in client commands first, then skills commands, then prompt commands (built-in +
            // user commands.json).
            var all = new List<ISlashCommand>();
            all.AddRange(ClientCommands.BuiltIns());
            all.AddRange(SkillCommands.BuiltIns());
            all.AddRange(SlashCommandConfig.LoadMerged(userJson, LoggerSink.Instance));

            _slashRegistry = new SlashCommandRegistry(all);
            _slashProcessor = new SlashCommandProcessor(_slashRegistry);
            _slashContext = new MainFormSlashContext(this);
        }

        // ===== ISlashCommandContext backing (called via MainFormSlashContext) =====

        internal string SlashWorkingDir()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null) return null;
            // The tab's WorkingDir is the canonical value (what the MCP servers receive); fall back to the
            // conversation's copy only if the tab field is empty.
            if (!string.IsNullOrEmpty(c.WorkingDir)) return c.WorkingDir;
            return c.Conversation != null ? c.Conversation.WorkingDir : null;
        }

        internal bool SlashHasServer(string serverName)
        {
            return _mcpRegistry != null && _mcpRegistry.HasServer(serverName);
        }

        internal void SlashWriteInfo(string text)
        {
            try
            {
                var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (c != null && c.Transcript != null)
                    c.Transcript.AddMessage(MessageRole.Tool, text ?? string.Empty); // chromeless, like tool activity
            }
            catch { }
        }

        internal IList<string> SlashGetModels()
        {
            var list = AppSettings.GetList("models");
            if (list == null || list.Count == 0) list = ModelDefaults.ModelList();
            return list ?? new List<string>();
        }

        internal string SlashGetActiveModel()
        {
            return GetSelectedModel();
        }

        internal void SlashSetModel(string slug)
        {
            if (string.IsNullOrEmpty(slug)) return;
            try
            {
                if (this.cmbModel != null)
                {
                    _syncingModelCombo = true;
                    try { this.cmbModel.Text = slug; }
                    finally { _syncingModelCombo = false; }
                }
                var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
                if (c != null)
                {
                    c.SelectedModel = slug;
                    if (c.Conversation != null) c.Conversation.SelectedModel = slug;
                }
            }
            catch { }
        }

        // Built-in MCP tool servers that can be toggled by name with /tool (custom mcp.json servers are
        // presence-based; memory is intentionally excluded -- it's a feature toggle, not a tool server).
        internal IList<string> SlashGetServerNames()
        {
            return new List<string>(new string[]
            {
                McpConfig.WebName, McpConfig.FilesName, McpConfig.GitName,
                McpConfig.CommandName, McpConfig.MsBuildName, McpConfig.SkillsName, McpConfig.GitHubName
            });
        }

        // (settings key, default-on) for a server name, or null/false for an unknown name.
        private static string ServerSettingKey(string name, out bool defaultOn)
        {
            defaultOn = false;
            if (string.Equals(name, McpConfig.WebName, StringComparison.OrdinalIgnoreCase)) return "mcp_web_enabled";
            if (string.Equals(name, McpConfig.FilesName, StringComparison.OrdinalIgnoreCase)) return "mcp_files_enabled";
            if (string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase)) { defaultOn = true; return "mcp_git_enabled"; }
            if (string.Equals(name, McpConfig.CommandName, StringComparison.OrdinalIgnoreCase)) return "mcp_command_enabled";
            if (string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase)) { defaultOn = true; return "mcp_msbuild_enabled"; }
            if (string.Equals(name, McpConfig.MemoryName, StringComparison.OrdinalIgnoreCase)) return "mcp_memory_enabled";
            if (string.Equals(name, McpConfig.SkillsName, StringComparison.OrdinalIgnoreCase)) return "mcp_skills_enabled";
            if (string.Equals(name, McpConfig.GitHubName, StringComparison.OrdinalIgnoreCase)) return "mcp_github_enabled";
            return null;
        }

        internal bool SlashGetServerEnabled(string name)
        {
            bool defaultOn;
            string key = ServerSettingKey(name, out defaultOn);
            if (key == null) return false;
            bool stored = AppSettings.GetBool(key, defaultOn);
            // Git/MSBuild are force-off when the underlying tool isn't installed, mirroring RebuildMcpHost.
            if (string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase)) return stored && GitProbe.IsInstalled();
            if (string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase)) return stored && MsBuildProbe.IsInstalled();
            return stored;
        }

        internal string SlashSetServerEnabled(string name, bool enabled)
        {
            bool defaultOn;
            string key = ServerSettingKey(name, out defaultOn);
            if (key == null) return "Unknown server: " + name;

            if (enabled && string.Equals(name, McpConfig.GitName, StringComparison.OrdinalIgnoreCase) && !GitProbe.IsInstalled())
                return "git is not installed, so the git server can't be enabled.";
            if (enabled && string.Equals(name, McpConfig.MsBuildName, StringComparison.OrdinalIgnoreCase) && !MsBuildProbe.IsInstalled())
                return "MSBuild was not found, so the msbuild server can't be enabled.";
            if (enabled && string.Equals(name, McpConfig.GitHubName, StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(AppSettings.GetString("mcp_github_pat")))
                return "Set a GitHub PAT in Settings before enabling the github server.";

            AppSettings.SetBool(key, enabled);
            RebuildMcpHost(); // reconnects with the new server set (connects on a background thread)
            return null;
        }

        // ---- skills: per-conversation override accessors backed by the active tab's Conversation ----

        internal bool? SlashGetConversationSkillsFeatureOff()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            return (c != null && c.Conversation != null) ? c.Conversation.SkillsFeatureOff : null;
        }

        internal void SlashSetConversationSkillsFeatureOff(bool? value)
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            c.Conversation.SkillsFeatureOff = value;
            SaveConversationContext(c);
        }

        internal IDictionary<string, bool> SlashGetConversationSkillOverrides()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            var src = (c != null && c.Conversation != null) ? c.Conversation.SkillOverrides : null;
            return src != null
                ? new Dictionary<string, bool>(src, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        internal void SlashSetConversationSkillOverride(string slug, bool? value)
        {
            if (string.IsNullOrEmpty(slug)) return;
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            if (c.Conversation.SkillOverrides == null)
                c.Conversation.SkillOverrides = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            if (value.HasValue) c.Conversation.SkillOverrides[slug] = value.Value;
            else c.Conversation.SkillOverrides.Remove(slug);
            SaveConversationContext(c);
        }

        internal void SlashResetConversationSkills()
        {
            var c = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (c == null || c.Conversation == null) return;
            c.Conversation.SkillsFeatureOff = null;
            if (c.Conversation.SkillOverrides != null) c.Conversation.SkillOverrides.Clear();
            SaveConversationContext(c);
        }

        // Persist a conversation after a slash-command edit, honoring the help-template no-save guard.
        private static void SaveConversationContext(TabManager.ChatTabContext c)
        {
            try { if (c != null && !c.NoSaveUntilUserSend) ConversationStore.Save(c.Conversation); }
            catch { }
        }

        internal void SlashNewConversation()
        {
            if (_tabManager != null) _tabManager.CreateConversationTab();
        }

        internal void SlashExportConversations()
        {
            ImportExportManager.ExportAll(this);
        }

        // ===== /compact: summarize the current conversation into a new tab =====

        private const string CompactionSystemPrompt =
            "You are a summarization assistant. Produce a concise but complete summary of the "
            + "conversation that captures the user's goals, key decisions, important facts, code, file "
            + "paths, and any open tasks or next steps. Write it so another assistant can resume the work "
            + "with full context. Output only the summary, with no preamble or commentary.";

        private const string CompactionContextPrefix =
            "The following is a summary of an earlier conversation, provided as context so you can "
            + "continue it seamlessly:\n\n";

        // Compaction always summarizes with gemini-flash (fast/cheap), regardless of the chat model.
        private const string CompactionModel = "~google/gemini-flash-latest";

        // Chromeless marker shown atop a conversation that /compact created. Persisted via
        // Conversation.ContinuedFromCompaction, so it re-renders on reopen (see RebuildTranscriptAsync).
        internal const string CompactionNoteText = "Continued from a compacted conversation.";

        internal void SlashCompact()
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null || ctx.Conversation == null || ctx.Transcript == null) return;

            string transcriptText = RenderHistoryForSummary(ctx.Conversation.History);
            if (string.IsNullOrEmpty(transcriptText))
            {
                ctx.Transcript.AddMessage(MessageRole.Tool, "Nothing to compact.");
                return;
            }
            if (_client == null || !_client.IsConfigured)
            {
                ctx.Transcript.AddMessage(MessageRole.Tool, "Cannot compact: client is not configured.");
                return;
            }

            // Chromeless progress line; remember its index so we can flip it to "Compacted" when done.
            int msgIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Tool, "Compacting conversation...");

            string model = CompactionModel;
            bool zdr = ResolveZdrForSend(ctx);

            List<ChatMessage> messages = new List<ChatMessage>();
            messages.Add(new ChatMessage("system", CompactionSystemPrompt));
            messages.Add(new ChatMessage("user",
                "Summarize the following conversation so a new session can continue it seamlessly.\n\n"
                + transcriptText));

            // Snapshot what the completion callback needs (the active tab may change before it returns).
            var sourceCtx = ctx;
            var transcriptRef = ctx.Transcript;
            int idx = msgIndex;

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                string summary = null, error = null;
                try
                {
                    string raw = _client.CreateCompletion(model, messages,
                        new ClientProperties { Zdr = zdr ? true : (bool?)null });
                    summary = ExtractCompletionContent(raw); // CreateCompletion returns the raw JSON body
                }
                catch (Exception ex) { error = ex.Message; }

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        try
                        {
                            if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(summary))
                            {
                                transcriptRef.UpdateMessageAt(idx,
                                    "Compaction failed" + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                                return;
                            }
                            transcriptRef.UpdateMessageAt(idx, "Compacted conversation.");
                            OpenCompactedConversation(sourceCtx, summary);
                        }
                        catch (Exception ex2)
                        {
                            try { transcriptRef.UpdateMessageAt(idx, "Compaction failed: " + ex2.Message); }
                            catch { }
                        }
                    });
                }
                catch { }
            });
        }

        // Open a new conversation tab seeded with the summary as a hidden system message (context). The
        // original conversation is left untouched. Inherits the source tab's working folder and model.
        private void OpenCompactedConversation(TabManager.ChatTabContext sourceCtx, string summary)
        {
            string wd = sourceCtx != null ? sourceCtx.WorkingDir : null;
            TabManager.ChatTabContext nctx;
            if (!string.IsNullOrEmpty(wd))
            {
                OpenNewTabWithWorkingDir(wd);
                nctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            }
            else
            {
                nctx = _tabManager != null ? _tabManager.CreateConversationTab() : null;
            }
            if (nctx == null || nctx.Conversation == null) return;

            // System-role history is sent to the model but never rendered (RebuildTranscript skips it),
            // so the summary rides along as invisible context.
            nctx.Conversation.History.Add(new ChatMessage("system", CompactionContextPrefix + summary));
            // Persist the "continued from compaction" marker so the note survives reopen.
            nctx.Conversation.ContinuedFromCompaction = true;

            // Continue with the source conversation's model.
            if (sourceCtx != null && !string.IsNullOrEmpty(sourceCtx.SelectedModel))
            {
                nctx.SelectedModel = sourceCtx.SelectedModel;
                nctx.Conversation.SelectedModel = sourceCtx.SelectedModel;
                try
                {
                    if (this.cmbModel != null)
                    {
                        _syncingModelCombo = true;
                        try { this.cmbModel.Text = sourceCtx.SelectedModel; }
                        finally { _syncingModelCombo = false; }
                    }
                }
                catch { }
            }

            if (nctx.Transcript != null)
                nctx.Transcript.AddMessage(MessageRole.Tool, CompactionNoteText);

            // Persist immediately. A compacted conversation already has meaningful content (the summary
            // context + marker), so it must survive closing/reopening and app restart even before the
            // first user send -- otherwise it stays NoSaveUntilUserSend and is never written to disk.
            string srcName = (sourceCtx != null && sourceCtx.Conversation != null) ? sourceCtx.Conversation.Name : null;
            if (!string.IsNullOrEmpty(srcName) && !string.Equals(srcName, "New Conversation", StringComparison.OrdinalIgnoreCase))
                nctx.Conversation.Name = srcName + " (compacted)";
            nctx.Conversation.LastUpdated = DateTime.Now; // sort to the top of the history list
            nctx.NoSaveUntilUserSend = false;
            try { ConversationStore.Save(nctx.Conversation); }
            catch { }

            // Reflect the name on the tab, and register this tab as the open instance of the persisted
            // conversation -- so double-clicking its history entry focuses this tab instead of opening a
            // duplicate (the dedup is keyed on Conversation.Id via TrackOpenConversation).
            try
            {
                string title = string.IsNullOrEmpty(nctx.Conversation.Name) ? "New Conversation" : nctx.Conversation.Name;
                if (nctx.Page != null) nctx.Page.Text = ZdrTitle(nctx.Conversation, title);
            }
            catch { }
            try { if (_sidebarManager != null) _sidebarManager.TrackOpenConversation(nctx.Conversation.Id, nctx.Page); }
            catch { }

            // Refresh the open history sidebar on a fresh UI message: a synchronous refresh here runs
            // inside the tab-creation/layout call stack and doesn't take, so the new entry would only
            // show after the sidebar is toggled. Deferring lets it settle first.
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                    catch { }
                });
            }
            catch { }
        }

        // Pull choices[0].message.content from a chat-completion JSON body (CreateCompletion returns the
        // raw body). Mirrors Conversation.ExtractTitleFromJson; uses JavaScriptSerializer like that path.
        private static string ExtractCompletionContent(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try
            {
                var ser = new System.Web.Script.Serialization.JavaScriptSerializer();
                var root = ser.DeserializeObject(json) as Dictionary<string, object>;
                if (root == null || !root.ContainsKey("choices")) return null;
                var choices = root["choices"] as object[];
                if (choices == null || choices.Length == 0) return null;
                var first = choices[0] as Dictionary<string, object>;
                if (first == null) return null;
                if (first.ContainsKey("message"))
                {
                    var msg = first["message"] as Dictionary<string, object>;
                    if (msg != null && msg.ContainsKey("content"))
                        return msg["content"] as string;
                }
                if (first.ContainsKey("text"))
                    return first["text"] as string;
            }
            catch { }
            return null;
        }

        private static string RenderHistoryForSummary(IList<ChatMessage> history)
        {
            if (history == null) return string.Empty;
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < history.Count; i++)
            {
                var m = history[i];
                if (m == null) continue;
                string role = (m.Role ?? string.Empty).ToLowerInvariant();
                if (role != "user" && role != "assistant") continue;
                string content = m.Content ?? string.Empty;
                if (content.Trim().Length == 0) continue;
                sb.Append(role == "user" ? "User: " : "Assistant: ").Append(content).Append("\n\n");
            }
            return sb.ToString().Trim();
        }

        // Accessors used by the autocomplete popup. Both ensure the subsystem is built first, so the
        // popup works on the very first keystroke (before any send has occurred).
        internal SlashCommandRegistry GetSlashRegistry()
        {
            EnsureSlashCommands();
            return _slashRegistry;
        }

        internal ISlashCommandContext GetSlashContext()
        {
            EnsureSlashCommands();
            return _slashContext;
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null) return;

            // Validate input
            string baseText = _inputManager != null ? (_inputManager.GetInputText() ?? string.Empty) : string.Empty;
            string text = baseText;
            if (ctx.IsSending) return; // ensure only one in-flight request per tab

            // Slash-command interception. Only a leading slash (position 0) is a command; prompt
            // commands expand in place (the expansion becomes the actual user message), while gated or
            // invalid commands are surfaced without sending. Unknown "/foo" returns null and is sent
            // literally, so ordinary messages that happen to start with "/" are not hijacked.
            if (baseText.Length > 0 && baseText[0] == '/')
            {
                EnsureSlashCommands();
                var slash = _slashProcessor.Process(baseText, _slashContext);
                if (slash != null)
                {
                    if (slash.Error != null)
                    {
                        // Surface the reason as a chromeless (tool-style) line; keep the typed command so
                        // the user can correct it (e.g. fix the path or enable the server).
                        ctx.Transcript.AddMessage(MessageRole.Tool, slash.Error);
                        return;
                    }
                    if (!slash.SendToModel)
                    {
                        // Client command handled locally (none in v1, but the seam is live).
                        if (_inputManager != null) _inputManager.ClearInput();
                        return;
                    }
                    // Prompt expansion: send the template instead of the typed "/command".
                    baseText = slash.TextToSend;
                    text = slash.TextToSend;
                }
            }

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

                // Effective ZDR for this send (global default OR the per-conversation toggle); engages
                // the one-way latch on the first ZDR send and tags the message bubbles just added.
                bool zdrForSend = ResolveZdrForSend(ctx);
                if (zdrForSend) MarkActiveTurnZdrBubbles(ctx);

                // Tool-enabled turn: tool activity renders as a separate chrome-less message above the
                // answer bubble. BeginToolSend owns the whole turn; the plain path below is unchanged.
                // Skills also route through the tool loop (they inject a manifest and expose open_skill),
                // so a conversation with skills but no MCP tools still takes this path.
                bool hasMcpTools = _mcpRegistry != null && _mcpRegistry.HasTools;
                if (hasMcpTools || ConversationHasSkills(ctx))
                {
                    BeginToolSend(ctx, modelToUse, zdrForSend);
                    return;
                }

                // Add placeholder assistant message to stream into and capture its index
                int assistantIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Assistant, string.Empty);
                if (zdrForSend) ctx.Transcript.SetMessageZdrTag(assistantIndex, true);
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

                        _client.CreateCompletionStream(
                            modelToUse,
                            snapshot,
                            new ClientProperties { Stream = true, Zdr = zdrForSend ? true : (bool?)null },
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

        // ---------------- ZDR (zero data retention) per-conversation plumbing ----------------

        // Effective ZDR for the active conversation's next send: the global default OR the per-tab
        // toggle. On the first send where ZDR is effective, engages the one-way latch (recording the
        // history index of the user message being sent) so the checkbox locks on and the tab + every
        // message from here on are marked ZDR. Returns whether ZDR applies to this send.
        private bool ResolveZdrForSend(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Conversation == null) return false;
            // The per-conversation toggle (seeded from the global default at creation) is the source of
            // truth; a latched conversation stays ZDR for good. The global default is not re-applied here,
            // so unchecking the box before the first send genuinely disables ZDR for this conversation.
            bool zdr = ctx.Conversation.Zdr || ConvIsZdrLatched(ctx);
            if (!zdr) return false;

            if (ctx.Conversation.ZdrFirstMessageIndex < 0)
            {
                int idx = LastUserHistoryIndex(ctx.Conversation);
                ctx.Conversation.ZdrFirstMessageIndex =
                    idx >= 0 ? idx : Math.Max(0, ctx.Conversation.History.Count - 1);
                try { if (!ctx.NoSaveUntilUserSend) ConversationStore.Save(ctx.Conversation); }
                catch { }
                // Reflect the now-latched state: lock the checkbox and mark the tab + sidebar.
                try { SyncZdrCheckboxFromActiveTab(); }
                catch { }
                try { UpdateTabTitleForZdr(ctx); }
                catch { }
                try { if (_sidebarManager != null) _sidebarManager.RefreshSidebarList(); }
                catch { }
            }
            return true;
        }

        private static int LastUserHistoryIndex(Conversation convo)
        {
            var h = convo.History;
            for (int i = h.Count - 1; i >= 0; i--)
                if (h[i] != null && string.Equals(h[i].Role, "user", StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // True once a conversation has latched ZDR: from that point every user/assistant message is a
        // zero-retention message (the checkbox is locked on and ZDR never turns back off).
        private static bool ConvIsZdrLatched(TabManager.ChatTabContext ctx)
        {
            return ctx != null && ctx.Conversation != null && ctx.Conversation.ZdrFirstMessageIndex >= 0;
        }

        // Tags the user bubble just added for this turn (assistant bubbles are tagged as they
        // materialize). No-op when the conversation isn't ZDR-latched.
        private void MarkActiveTurnZdrBubbles(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Transcript == null) return;
            try { ctx.Transcript.MarkLastUserMessageZdr(); }
            catch { }
        }

        // Marker prefixed to a tab title / sidebar row once a conversation has latched ZDR.
        internal const string ZdrTitlePrefix = "[zdr] ";

        // The display title for a conversation: its name, prefixed with the ZDR marker once latched.
        internal static string ZdrTitle(Conversation convo, string name)
        {
            string n = string.IsNullOrEmpty(name) ? "Conversation" : name;
            return (convo != null && convo.ZdrFirstMessageIndex >= 0) ? ZdrTitlePrefix + n : n;
        }

        // Recompute the active tab's title to reflect a newly-latched ZDR state.
        private void UpdateTabTitleForZdr(TabManager.ChatTabContext ctx)
        {
            if (ctx == null || ctx.Page == null || ctx.Conversation == null) return;
            try { ctx.Page.Text = ZdrTitle(ctx.Conversation, ctx.Conversation.Name); }
            catch { }
        }

        // Owner-draw the model combo so only the model name shows (after the last "/"), while the item
        // value stays the full "author/model" id.
        private void cmbModel_DrawItem(object sender, DrawItemEventArgs e)
        {
            e.DrawBackground();
            if (e.Index >= 0 && this.cmbModel != null && e.Index < this.cmbModel.Items.Count)
            {
                string full = Convert.ToString(this.cmbModel.Items[e.Index]);
                // Clip the name at the edge like a native combo (no ellipsis).
                TextRenderer.DrawText(e.Graphics, ShortModelName(full), e.Font, e.Bounds, e.ForeColor,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            }
            e.DrawFocusRectangle();
        }

        // "author/model-name" -> "model-name"; passes through anything without a slash.
        internal static string ShortModelName(string full)
        {
            if (string.IsNullOrEmpty(full)) return full ?? string.Empty;
            int slash = full.LastIndexOf('/');
            return (slash >= 0 && slash < full.Length - 1) ? full.Substring(slash + 1) : full;
        }

        // Per-tab ZDR checkbox <-> active conversation. Checked = the conversation's own ZDR toggle
        // (seeded from the global default when the conversation was created), or locked on once latched.
        // Disabled only when the conversation has latched ZDR — the global default no longer locks the
        // box, so the user can uncheck it before the first send.
        private bool _syncingZdr;
        private void SyncZdrCheckboxFromActiveTab()
        {
            if (this.chkZdrTab == null) return;
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            bool latched = ConvIsZdrLatched(ctx);
            bool convZdr = ctx != null && ctx.Conversation != null && ctx.Conversation.Zdr;
            _syncingZdr = true;
            try
            {
                this.chkZdrTab.Checked = convZdr || latched;
                this.chkZdrTab.Enabled = ctx != null && ctx.Conversation != null && !latched;
            }
            finally { _syncingZdr = false; }
        }

        private void chkZdrTab_CheckedChanged(object sender, EventArgs e)
        {
            if (_syncingZdr) return;
            var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
            if (ctx == null || ctx.Conversation == null) return;
            ctx.Conversation.Zdr = this.chkZdrTab.Checked;
            // Persist only for an already-saved conversation; a brand-new empty tab persists the choice
            // on its first send (the in-memory flag is still honored until then).
            try
            {
                if (!ctx.NoSaveUntilUserSend && ctx.Conversation.History != null && ctx.Conversation.History.Count > 0)
                    ConversationStore.Save(ctx.Conversation);
            }
            catch { }
        }

        // True when this conversation has at least one ENABLED skill (bundled or project). Used to route
        // the turn through the tool loop even with no MCP tools, since skills inject a manifest and expose
        // open_skill there. Cheap (a couple of directory scans + skills.json); BeginToolSend re-resolves
        // the enabled set it actually uses, so this is just the gate.
        private static bool ConversationHasSkills(TabManager.ChatTabContext ctx)
        {
            try
            {
                string workdir = (ctx != null) ? ctx.WorkingDir : null;
                SkillCatalog cat = SkillInjection.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, workdir);
                Conversation convo = (ctx != null) ? ctx.Conversation : null;
                return SkillResolve.EnabledSkills(cat.Skills, SkillEnablement.LoadGlobal(),
                    convo != null ? convo.SkillsFeatureOff : null,
                    convo != null ? convo.SkillOverrides : null).Count > 0;
            }
            catch { return false; }
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

        private void BeginToolSend(TabManager.ChatTabContext ctx, string model, bool zdr)
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
                            _approvalStore,
                            _mcpRegistry) // classify third-party tools by their declared readOnly/destructive hints
                        : (IToolApprovalPolicy)new AllowAllApprovalPolicy();
                    var orch = new McpChatOrchestrator(_client, _mcpRegistry, approval,
                                                       model, LoggerSink.Instance);
                    orch.WorkingDir = ctx.WorkingDir;
                    orch.Zdr = zdr ? true : (bool?)null;
                    // At the tool-call cap, confirm in-transcript (like a tool-approval prompt) whether
                    // to continue: Continue grants another batch, Stop has the model wrap up and ask how
                    // to proceed. Needs this tab's panel to host the prompt; without one the orchestrator
                    // wraps up by default.
                    if (ctx.ApprovalPanel != null)
                    {
                        var contPrompt = new TranscriptContinuationPrompt(this, delegate { return ctx.ApprovalPanel; });
                        orch.ContinuationDecider = delegate(int n) { return contPrompt.Ask(n); };
                    }
                    orch.RequestMessageTransform = delegate(IList<ChatMessage> h)
                    {
                        List<ChatMessage> asList = h as List<ChatMessage>;
                        if (asList == null) asList = new List<ChatMessage>(h);
                        return BuildMessagesForModel(asList);
                    };
                    // Inject the workspace's persistent memory index (rebuilt from .gxpt/memory.md each
                    // request) only when memory is enabled and this conversation has a workspace.
                    if (AppSettings.GetBool("mcp_memory_enabled", false) && !string.IsNullOrEmpty(ctx.WorkingDir))
                    {
                        string memWorkdir = ctx.WorkingDir;
                        orch.MemorySystemMessageProvider = delegate { return MemoryInjection.Build(memWorkdir); };
                    }

                    // Skills: discover bundled (<exe>/skills) + project (<workdir>/.gxpt/skills) skills for
                    // this turn, resolve which are enabled (global skills.json default; the per-conversation
                    // override layer lands in phase 4b), then inject the manifest and expose open_skill over
                    // the enabled set. Rebuilt per send, so on-disk edits take effect on the next turn.
                    SkillCatalog skillCatalog =
                        SkillInjection.BuildCatalog(AppDomain.CurrentDomain.BaseDirectory, ctx.WorkingDir);
                    Conversation skillConvo = ctx.Conversation;
                    List<Skill> enabledSkills = SkillResolve.EnabledSkills(
                        skillCatalog.Skills, SkillEnablement.LoadGlobal(),
                        skillConvo != null ? skillConvo.SkillsFeatureOff : null,
                        skillConvo != null ? skillConvo.SkillOverrides : null);
                    if (enabledSkills.Count > 0)
                    {
                        List<Skill> enabledForTurn = enabledSkills;
                        orch.SkillsManifestSystemMessageProvider =
                            delegate { return SkillInjection.BuildManifestMessage(enabledForTurn); };
                        // open_skill is enabled-scoped; read_skill_file spans the whole catalog.
                        orch.SkillTools = new SkillTools(enabledForTurn, skillCatalog);
                    }
                    // The skill-authoring tools are owned by the skill-writer meta-skill: omit them from
                    // context unless that skill is enabled for this conversation.
                    ICollection<string> hiddenTools = SkillMeta.HiddenTools(enabledSkills);
                    if (hiddenTools.Count > 0) orch.HiddenToolNames = hiddenTools;

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
                    // Tool activity renders in two beats: an immediate "using <tool>" placeholder on the
                    // call (live feedback while it runs / the approval gate waits), replaced by the
                    // outcome marker on the result. So a denied call ends as "denied: <tool>" and an
                    // approved edit ends as its collapsible record — never an unapproved diff. The
                    // placeholder sits at the tail of the tool block (calls are serial and no model text
                    // streams between a call and its result), so we truncate back to it and append.
                    LiveSeg pendingToolSeg = null;
                    int pendingToolStart = 0;
                    Action<string> onToolCall = delegate(string name)
                    {
                        lock (sbLock)
                        {
                            LiveSeg cur = (segs.Count > 0) ? segs[segs.Count - 1] : null;
                            if (cur == null || cur.Role != MessageRole.Tool)
                            {
                                cur = new LiveSeg(MessageRole.Tool);
                                segs.Add(cur);
                            }
                            if (cur.Text.Length > 0) cur.Text.Append("\r\n");
                            pendingToolSeg = cur;
                            pendingToolStart = cur.Text.Length;
                            cur.Text.Append(McpMarkers.Call(name));
                        }
                    };
                    // argsJson is threaded through for files__edit etc., which render a collapsible
                    // record instead of the generic "using" marker; register the record (it has its own
                    // lock) before taking sbLock. Live keys are per-call GUIDs (ephemeral — the reloaded
                    // view re-derives under the persisted call id).
                    Action<string, string, string, bool> onToolResult = delegate(string name, string argsJson, string resultText, bool isError)
                    {
                        string marker = McpMarkers.IsDenied(resultText)
                            ? McpMarkers.Denied(name)
                            : EditDiffMarkerOrCall(ctx.Transcript, name, argsJson, Guid.NewGuid().ToString("N"));
                        lock (sbLock)
                        {
                            if (pendingToolSeg != null)
                            {
                                // Replace the placeholder (at the tail) with the outcome marker.
                                if (pendingToolStart <= pendingToolSeg.Text.Length)
                                    pendingToolSeg.Text.Length = pendingToolStart;
                                pendingToolSeg.Text.Append(marker);
                                pendingToolSeg = null;
                            }
                            else
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

                    orch.RunTurn(ctx.Conversation.History, new DelegateToolLoopUi(onAppend, onToolCall, onToolResult, onComplete, onErr));
                }
                catch (Exception ex)
                {
                    // The turn worker crashed (e.g. in EnsureWorkingDir / BuildMessagesForModel /
                    // RunTurn). The user sees ex.Message in the transcript, but log the full exception
                    // (type + stack) too — otherwise a worker crash leaves no trace in the log.
                    try { LoggerSink.Instance.Log("mcp", "turn worker failed: " + ex); }
                    catch { }
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
                    // Once the conversation has latched ZDR, every assistant bubble it produces is a
                    // zero-retention message — tag it as it materializes (tool blocks are not tagged).
                    if (seg.Role == MessageRole.Assistant && ConvIsZdrLatched(ctx))
                        ctx.Transcript.SetMessageZdrTag(seg.Index, true);
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

                // Fresh install (no models configured yet): fall back to the shared default catalog so
                // the combo is never empty and matches what the Settings seed will write.
                if (list == null || list.Count == 0) list = ModelDefaults.ModelList();
                if (string.IsNullOrEmpty(def)) def = ModelDefaults.DefaultModel;

                if (this.cmbModel != null)
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

        // Rebuilds the "Open Recent Work Dir" submenu each time the File menu opens. Lists each
        // remembered dir (full path) that still exists; silently drops missing ones. Disables the
        // parent when nothing valid remains.
        private void PopulateRecentWorkDirsMenu()
        {
            if (miOpenRecentWorkDir == null) return;

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
                try { exists = Directory.Exists(dir); }
                catch { exists = false; }

                string captured = dir; // avoid the closure capturing the loop variable
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

        private void miFile_DropDownOpening(object sender, EventArgs e)
        {
            PopulateRecentWorkDirsMenu();
        }

        // File > Close Conversation
        private void closeConversationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (_tabManager != null) _tabManager.CloseActiveConversationTab();
        }

        // Build the cream notice strip and dock it at the bottom of the chat area (in pnlBottom, just
        // above the input - the same slot as the API-key banner). Styling mirrors WorkspaceContextStrip;
        // the font size is matched to the rest of the UI (ThemeManager scales the API-key banner the same way).
        private void InitModelUpdateBanner()
        {
            try
            {
                if (this.pnlBottom == null) return;

                _modelUpdateBanner = new System.Windows.Forms.Panel();
                _modelUpdateBanner.AutoSize = true;
                _modelUpdateBanner.Dock = DockStyle.Top;
                _modelUpdateBanner.Padding = new Padding(6, 4, 6, 4);
                _modelUpdateBanner.BackColor = Color.FromArgb(252, 246, 220); // cream / warning
                _modelUpdateBanner.Visible = false;

                // The default control font is noticeably small; match the configured chat font size so this
                // reads like the rest of the UI (and the API-key banner, which ThemeManager scales likewise).
                try
                {
                    double fs = AppSettings.GetDouble("font_size", 0);
                    float size = (fs > 0) ? (float)Math.Max(6, Math.Min(48, fs)) : 9f;
                    _modelUpdateBanner.Font = new Font(_modelUpdateBanner.Font.FontFamily, size, _modelUpdateBanner.Font.Style);
                }
                catch { }

                var flow = new FlowLayoutPanel();
                flow.AutoSize = true;
                flow.Dock = DockStyle.Fill;
                flow.FlowDirection = FlowDirection.LeftToRight;
                flow.WrapContents = false;
                flow.Margin = new Padding(0);

                var text = new Label();
                text.AutoSize = true;
                text.TextAlign = ContentAlignment.MiddleLeft;
                text.ForeColor = Color.FromArgb(55, 55, 55);
                text.Margin = new Padding(3, 0, 3, 0);
                text.Anchor = AnchorStyles.None; // vertically centered in the flow row
                text.Text = "Updated recommended models are available.";

                var review = MakeBannerLink("Review in Settings");
                review.LinkClicked += delegate { OnModelBannerReview(); };
                var dismiss = MakeBannerLink("Dismiss");
                dismiss.LinkClicked += delegate { OnModelBannerDismiss(); };

                flow.Controls.Add(text);
                flow.Controls.Add(review);
                flow.Controls.Add(dismiss);
                _modelUpdateBanner.Controls.Add(flow);

                // Host in pnlBottom. The input box (and other banners) are docked Bottom, so docking this
                // Top pins it to the top of pnlBottom - i.e. the bottom of the transcript, directly above
                // the input area - regardless of child add order. BringToFront so the Top dock wins.
                this.pnlBottom.Controls.Add(_modelUpdateBanner);
                _modelUpdateBanner.BringToFront();

                // Let the input-height math account for this banner's footprint, like the other banners.
                if (_inputManager != null) _inputManager.SetModelUpdateBanner(_modelUpdateBanner);
            }
            catch { }
        }

        private static LinkLabel MakeBannerLink(string text)
        {
            var lnk = new LinkLabel();
            lnk.AutoSize = true;
            lnk.Text = text;
            lnk.TextAlign = ContentAlignment.MiddleLeft;
            lnk.LinkColor = Color.FromArgb(0, 90, 158);
            lnk.ActiveLinkColor = Color.FromArgb(0, 90, 158);
            lnk.VisitedLinkColor = Color.FromArgb(0, 90, 158);
            lnk.LinkBehavior = LinkBehavior.HoverUnderline;
            lnk.Margin = new Padding(12, 0, 3, 0);
            lnk.Anchor = AnchorStyles.None; // vertically centered in the flow row
            return lnk;
        }

        // Show the banner when the shipped recommended list differs from what the user last acknowledged
        // (or has never acknowledged one). The acknowledged fingerprint is only written when the user acts
        // (reviews or dismisses), so the banner persists across launches until then.
        private void MaybeShowModelUpdateBanner()
        {
            try
            {
                if (_modelUpdateBanner == null) return;
                string seen = AppSettings.GetString("recommended_hash_seen");
                string current = ModelDefaults.RecommendedHash();
                bool show = string.IsNullOrEmpty(seen) || !string.Equals(seen, current, StringComparison.Ordinal);
                _modelUpdateBanner.Visible = show;
            }
            catch { }
        }

        private void MarkRecommendedSeen()
        {
            try { AppSettings.SetString("recommended_hash_seen", ModelDefaults.RecommendedHash()); }
            catch { }
            if (_modelUpdateBanner != null) _modelUpdateBanner.Visible = false;
        }

        private void OnModelBannerDismiss()
        {
            MarkRecommendedSeen();
        }

        private void OnModelBannerReview()
        {
            MarkRecommendedSeen();
            miSettings_Click(this, EventArgs.Empty);
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
                // Re-sync the per-tab checkbox after settings may have changed (the global default
                // seeds new conversations; it does not retroactively re-lock this one).
                SyncZdrCheckboxFromActiveTab();
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
            return ModelDefaults.DefaultModel;
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

            // MSBuild tools are named per discovered engine/IDE (msbuild__build_4_0,
            // msbuild__build_solution_2022, ...), so they can't be cased in the switch below; render them
            // from the name + args here. Solution (devenv) builds are checked first - the more specific
            // prefix - then the per-engine MSBuild builds.
            if (name != null && name.StartsWith("msbuild__build_solution_", StringComparison.Ordinal))
                return BuildDevenvRecord(name, args, out header, out body, out language);
            if (name != null && name.StartsWith("msbuild__build_", StringComparison.Ordinal))
                return BuildMsBuildRecord(name, args, out header, out body, out language);

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
                case "git__fetch":
                {
                    string remote = Str(args, "remote");
                    header = remote.Length > 0 ? ("Fetched from " + remote) : "Fetched from remote"; return true;
                }
                case "git__pull":
                {
                    string remote = Str(args, "remote"); string branch = Str(args, "branch");
                    string tgt = remote.Length > 0 ? (branch.Length > 0 ? remote + "/" + branch : remote) : branch;
                    header = (Bool(args, "rebase") ? "Pulled (rebase)" : "Pulled") + (tgt.Length > 0 ? " from " + tgt : ""); return true;
                }
                case "git__checkout":
                {
                    string r = Str(args, "ref"); if (r.Length == 0) return false;
                    header = Bool(args, "create") ? ("Created branch " + r) : ("Switched to " + r); return true;
                }
                case "git__restore":
                {
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = (Bool(args, "staged") ? "Unstaged " : "Restored ") + paths; return true;
                }
                case "git__branch":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "list";
                    string nm = Str(args, "name");
                    switch (action.ToLowerInvariant())
                    {
                        case "create": header = nm.Length > 0 ? ("Created branch " + nm) : "Created branch"; break;
                        case "delete": header = nm.Length > 0 ? ("Deleted branch " + nm) : "Deleted branch"; break;
                        case "rename": header = "Renamed branch" + (Str(args, "new_name").Length > 0 ? " to " + Str(args, "new_name") : ""); break;
                        default: header = "Listed branches"; break;
                    }
                    return true;
                }
                case "git__merge":
                {
                    string b = Str(args, "branch"); if (b.Length == 0) return false;
                    header = "Merged " + b; return true;
                }
                case "git__rebase":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "start";
                    string onto = Str(args, "onto");
                    switch (action.ToLowerInvariant())
                    {
                        case "continue": header = "Continued rebase"; break;
                        case "abort": header = "Aborted rebase"; break;
                        case "skip": header = "Skipped rebase commit"; break;
                        default: header = onto.Length > 0 ? ("Rebased onto " + onto) : "Rebased"; break;
                    }
                    return true;
                }
                case "git__cherry_pick":
                {
                    string c = Str(args, "commit"); if (c.Length == 0) return false;
                    header = "Cherry-picked " + c; return true;
                }
                case "git__add":
                {
                    if (Bool(args, "all")) { header = "Staged all changes"; return true; }
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = "Staged " + paths; return true;
                }
                case "git__reset":
                {
                    string paths = JoinPaths(args, "paths");
                    if (paths.Length > 0) { header = "Unstaged " + paths; return true; }
                    string mode = Str(args, "mode"); if (mode.Length == 0) mode = "mixed";
                    string target = Str(args, "target");
                    header = "Reset (" + mode.ToLowerInvariant() + ")" + (target.Length > 0 ? " to " + target : ""); return true;
                }
                case "git__rm":
                {
                    string paths = JoinPaths(args, "paths"); if (paths.Length == 0) return false;
                    header = (Bool(args, "cached") ? "Unstaged (kept) " : "Removed ") + paths; return true;
                }
                case "git__stash":
                {
                    string action = Str(args, "action"); if (action.Length == 0) action = "push";
                    switch (action.ToLowerInvariant())
                    {
                        case "pop": header = "Popped stash"; break;
                        case "apply": header = "Applied stash"; break;
                        case "drop": header = "Dropped stash"; break;
                        case "list": header = "Listed stashes"; break;
                        default: header = "Stashed changes"; break;
                    }
                    return true;
                }
                case "reveal_tools": header = "Checking available tools"; return true;
                case "open_skill":
                {
                    // Post-flight (completed) record label, so past tense like the other records.
                    string slugs = JoinPaths(args, "names"); // generic string-array joiner
                    header = slugs.Length > 0 ? "Read skill: " + slugs : "Read skill";
                    return true;
                }
                case "read_skill_file":
                {
                    string rel = Str(args, "relpath");
                    header = rel.Length > 0 ? "Read skill file: " + rel : "Read skill file";
                    return true;
                }
                case "skills__run_skill_script":
                {
                    string rel = Str(args, "relpath");
                    header = rel.Length > 0 ? "Ran skill script: " + rel : "Ran a skill script";
                    return true;
                }
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

        // Comma-separated summary of a string[] pathspec arg (also accepts a lone string), for a
        // one-line tool-record header. Empty when absent.
        private static string JoinPaths(Newtonsoft.Json.Linq.JObject args, string name)
        {
            try
            {
                var t = args[name];
                var sb = new System.Text.StringBuilder();
                var arr = t as Newtonsoft.Json.Linq.JArray;
                if (arr != null)
                {
                    foreach (var p in arr)
                    {
                        string s = (string)p; if (string.IsNullOrEmpty(s)) continue;
                        if (sb.Length > 0) sb.Append(", ");
                        sb.Append(s);
                    }
                }
                else if (t != null && t.Type == Newtonsoft.Json.Linq.JTokenType.String)
                {
                    sb.Append((string)t);
                }
                return sb.ToString();
            }
            catch { return string.Empty; }
        }

        // Renders an MSBuild build-tool call (msbuild__build_<ver>) into a transcript record: a one-line
        // header like "Built MyApp.sln (Release · MSBuild 17.0 x64)" with the build options as a
        // collapsible body. The engine version comes from the tool-name suffix (build_17_0 -> 17.0).
        private static bool BuildMsBuildRecord(string name, Newtonsoft.Json.Linq.JObject args, out string header, out string body, out string language)
        {
            header = null; body = string.Empty; language = "text";

            string ver = name.Substring("msbuild__build_".Length).Replace('_', '.');
            string project = Str(args, "project");
            string projectLabel = project.Length > 0
                ? System.IO.Path.GetFileName(project.Replace('\\', '/')) : "solution";

            // Verb from the requested targets (Clean/Rebuild/Restore), defaulting to "Built".
            string targets = JoinPaths(args, "targets");
            string verb = "Built";
            switch (targets.ToLowerInvariant())
            {
                case "clean": verb = "Cleaned"; break;
                case "rebuild": verb = "Rebuilt"; break;
                case "restore": verb = "Restored"; break;
            }

            // Parenthetical: configuration, then "MSBuild <ver>[ <bitness>]".
            var bits = new List<string>();
            string config = Str(args, "configuration");
            if (config.Length > 0) bits.Add(config);
            string engine = "MSBuild " + ver;
            string bitness = Str(args, "bitness");
            if (bitness.Length > 0) engine += " " + bitness;
            bits.Add(engine);
            header = verb + " " + projectLabel + " (" + string.Join(" · ", bits.ToArray()) + ")";

            // Body: the build options, for the collapsible record (omitted when there's nothing extra).
            var lines = new List<string>();
            if (project.Length > 0) lines.Add("project: " + project);
            if (targets.Length > 0) lines.Add("targets: " + targets);
            if (config.Length > 0) lines.Add("configuration: " + config);
            string platform = Str(args, "platform");
            if (platform.Length > 0) lines.Add("platform: " + platform);
            var props = args["properties"] as Newtonsoft.Json.Linq.JObject;
            if (props != null)
                foreach (var kv in props)
                    lines.Add("property: " + kv.Key + "=" + (kv.Value != null ? kv.Value.ToString() : string.Empty));
            body = string.Join("\n", lines.ToArray());
            return true;
        }

        // Renders a devenv solution-build call (msbuild__build_solution_<year>) into a transcript record:
        // a header like "Built MyApp.sln (Release · Visual Studio 2022)" with the options as a body. The
        // VS year comes from the tool-name suffix (build_solution_2022 -> 2022).
        private static bool BuildDevenvRecord(string name, Newtonsoft.Json.Linq.JObject args, out string header, out string body, out string language)
        {
            header = null; body = string.Empty; language = "text";

            string year = name.Substring("msbuild__build_solution_".Length);
            string solution = Str(args, "solution");
            string solutionLabel = solution.Length > 0
                ? System.IO.Path.GetFileName(solution.Replace('\\', '/')) : "solution";

            // Verb from the action (Rebuild/Clean/Deploy), defaulting to "Built".
            string action = Str(args, "action");
            string verb = "Built";
            switch (action.ToLowerInvariant())
            {
                case "rebuild": verb = "Rebuilt"; break;
                case "clean": verb = "Cleaned"; break;
                case "deploy": verb = "Deployed"; break;
            }

            // Parenthetical: configuration (default Release), then "Visual Studio <year>".
            var bits = new List<string>();
            string config = Str(args, "configuration");
            if (config.Length == 0) config = "Release";
            string platform = Str(args, "platform");
            bits.Add(platform.Length > 0 ? config + "|" + platform : config);
            bits.Add("Visual Studio " + year);
            header = verb + " " + solutionLabel + " (" + string.Join(" · ", bits.ToArray()) + ")";

            // Body: the build options, for the collapsible record.
            var lines = new List<string>();
            if (solution.Length > 0) lines.Add("solution: " + solution);
            if (action.Length > 0) lines.Add("action: " + action);
            lines.Add("configuration: " + (platform.Length > 0 ? config + "|" + platform : config));
            string project = Str(args, "project");
            if (project.Length > 0) lines.Add("project: " + project);
            body = string.Join("\n", lines.ToArray());
            return true;
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
                // Parallel to items: whether each emitted user/assistant bubble is a zero-retention
                // message (its source history index is at/after the ZDR latch). Tool blocks are false.
                var itemZdr = new List<bool>();
                int zdrLatch = convo.ZdrFirstMessageIndex;
                // A /compact conversation shows a persistent chromeless marker at the very top, rebuilt
                // from the saved flag (the live note added at creation isn't in history). ToolActivityRole
                // renders as MessageRole.Tool (chromeless); kept parallel with itemZdr.
                if (convo.ContinuedFromCompaction)
                {
                    items.Add(new ChatMessage(ToolActivityRole, CompactionNoteText));
                    itemZdr.Add(false);
                }
                // Consecutive tool-call runs that aren't separated by real assistant text merge into one
                // chrome-less block (e.g. reveal_tools followed by the call it enabled), so they read as
                // tightly as a single multi-call turn instead of two blocks with a gap between them. A
                // real assistant text bubble or a user message flushes the running block.
                System.Text.StringBuilder pendingTools = null;
                Action flushTools = delegate
                {
                    if (pendingTools != null && pendingTools.Length > 0)
                    {
                        items.Add(new ChatMessage(ToolActivityRole, pendingTools.ToString()));
                        itemZdr.Add(false);
                    }
                    pendingTools = null;
                };
                // Outcome of each tool call (by id) so a denied call renders as "denied" instead of as
                // an applied record. Tool messages aren't shown directly, but their content is the
                // result the orchestrator recorded (McpChatOrchestrator.DeniedResultText on denial).
                var toolResultById = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var rm in convo.History)
                {
                    if (rm == null) continue;
                    if (string.Equals(rm.Role, "tool", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(rm.ToolCallId))
                        toolResultById[rm.ToolCallId] = rm.Content;
                }
                int hi = -1;
                foreach (var m in convo.History)
                {
                    hi++;
                    if (m == null) continue;
                    string role = (m.Role ?? string.Empty).ToLowerInvariant();
                    if (role == "system" || role == "tool") continue; // not shown

                    bool z = zdrLatch >= 0 && hi >= zdrLatch;

                    if (role == "user")
                    {
                        flushTools();
                        items.Add(m);
                        itemZdr.Add(z);
                        continue;
                    }

                    // assistant text (intermediate or final) -> its own bubble, closing any tool run
                    if (!string.IsNullOrEmpty(m.Content) && m.Content.Trim().Length > 0)
                    {
                        flushTools();
                        items.Add(new ChatMessage("assistant", m.Content));
                        itemZdr.Add(z);
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
                            string res;
                            bool denied = !string.IsNullOrEmpty(tc.Id)
                                && toolResultById.TryGetValue(tc.Id, out res) && McpMarkers.IsDenied(res);
                            if (denied)
                            {
                                pendingTools.Append(McpMarkers.Denied(tc.Name));
                            }
                            else
                            {
                                string key = !string.IsNullOrEmpty(tc.Id) ? tc.Id : ("edit" + ti);
                                pendingTools.Append(EditDiffMarkerOrCall(ctx.Transcript, tc.Name, tc.ArgumentsJson, key));
                            }
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
                                local[k] = new ParsedMessage { Role = role, Text = text, Blocks = blocks, Attachments = att, Zdr = itemZdr[i + k] };
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
                    // The tab may have been closed (e.g. "Close Others") while this build was still
                    // running — its transcript is then disposed. Stop touching it, or BeginBatchUpdates/
                    // EndBatchUpdates would throw ObjectDisposedException.
                    if (ctx == null || ctx.Transcript == null || ctx.Transcript.IsDisposed)
                    {
                        try { timer.Stop(); timer.Dispose(); }
                        catch { }
                        return;
                    }

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
                            {
                                ctx.Transcript.AddParsedMessage(pm.Role, pm.Text, pm.Blocks, pm.Attachments);
                                if (pm.Zdr) ctx.Transcript.SetMessageZdrTag(ctx.Transcript.MessageCount - 1, true);
                            }
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
                ctx.Page.Text = ZdrTitle(convo, convo.Name);
                UpdateWindowTitleFromActiveTab();
            }
            catch { }

            // The tab was selected before the conversation was assigned, so re-sync the per-tab ZDR
            // checkbox now that we know this conversation's ZDR/latched state.
            SyncZdrCheckboxFromActiveTab();

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
                                    ctx.Page.Text = ZdrTitle(ctx.Conversation, name);
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
                    ctx.Page.Text = ZdrTitle(convo, convo.Name);
                    // Select this tab and update window title
                    SelectTab(ctx.Page);
                    UpdateWindowTitleFromActiveTab();
                }
                catch { }

                // Reusing the active blank tab won't re-fire OnTabSelected, so sync the ZDR checkbox
                // to the loaded conversation's state explicitly.
                SyncZdrCheckboxFromActiveTab();

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
                    // Help starts with the workspace strip hidden until a working folder is set.
                    convo.WorkspaceStripDismissed = true;
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
                    // Help starts with the workspace strip hidden until a working folder is set.
                    convo.WorkspaceStripDismissed = true;
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
