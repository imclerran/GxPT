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
        private const string HelpApiKeysId = "help:api_keys";
        private const string HelpPrivacyId = "help:privacy";

        // Manager classes for UI concerns
        private SidebarManager _sidebarManager;
        private TabManager _tabManager;
        private ThemeManager _themeManager;
        private InputManager _inputManager;
        // Attachments are tracked per-tab in TabManager.ChatTabContext.PendingAttachments

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
            catch { }
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
            UpdateWindowTitleFromActiveTab();
            SyncComboModelFromActiveTab();
            // Refresh the attachments banner to reflect the active tab's pending attachments
            RebuildAttachmentsBanner();
            if (_inputManager != null) _inputManager.FocusInputSoon();
        }

        private void OnTabsChanged()
        {
            if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
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
                int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
                int mw = (int)AppSettings.GetDouble("message_max_width", 700);
                if (tw <= 0) tw = 1000; if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                if (mw <= 0) mw = 700; if (mw < 100) mw = 100; if (mw > tw) mw = tw;
                // Primary designer transcript
                try
                {
                    if (this.chatTranscript != null)
                    {
                        this.chatTranscript.MaxContentWidth = tw;
                        this.chatTranscript.MaxBubbleWidth = mw;
                    }
                }
                catch { }
                // All tab transcripts
                try
                {
                    if (_tabManager != null)
                    {
                        foreach (var kv in _tabManager.TabContexts)
                        {
                            var t = kv.Value != null ? kv.Value.Transcript : null;
                            if (t == null) continue;
                            try { t.MaxContentWidth = tw; }
                            catch { }
                            try { t.MaxBubbleWidth = mw; }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            catch { }
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
                // Re-init client in case API key changed
                InitializeClient();
                UpdateApiKeyBanner();
                // Theme may have changed; re-apply to all open transcripts
                try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
                catch { }
                // Re-apply transcript/message widths in case they changed
                try
                {
                    int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
                    int mw = (int)AppSettings.GetDouble("message_max_width", 700);
                    if (tw <= 0) tw = 1000; if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                    if (mw <= 0) mw = 700; if (mw < 100) mw = 100; if (mw > tw) mw = tw;
                    if (this.chatTranscript != null) { this.chatTranscript.MaxContentWidth = tw; this.chatTranscript.MaxBubbleWidth = mw; }
                    if (_tabManager != null)
                    {
                        foreach (var kv in _tabManager.TabContexts)
                        {
                            var t = kv.Value != null ? kv.Value.Transcript : null;
                            if (t == null) continue;
                            try { t.MaxContentWidth = tw; }
                            catch { }
                            try { t.MaxBubbleWidth = mw; }
                            catch { }
                        }
                    }
                }
                catch { }
            }
        }

        // ===== Export/Import conversations (DotNetZip) =====
        private string ResolveConversationsFolderPath()
        {
            // Try to infer from existing items; fallback to %AppData%\GxPT\Conversations
            try
            {
                var items = ConversationStore.ListAll();
                var first = items.FirstOrDefault();
                if (first != null && !string.IsNullOrEmpty(first.Path))
                {
                    var dir = Path.GetDirectoryName(first.Path);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        try { Directory.CreateDirectory(dir); }
                        catch { }
                        return dir;
                    }
                }
            }
            catch { }

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string dirFallback = Path.Combine(Path.Combine(appData, "GxPT"), "Conversations");
            try { Directory.CreateDirectory(dirFallback); }
            catch { }
            return dirFallback;
        }

        // Designer wires this
        private void miExport_Click(object sender, EventArgs e)
        {
            string sourceDir = ResolveConversationsFolderPath();
            using (var sfd = new SaveFileDialog
            {
                Title = "Export Conversations",
                Filter = "GxPT Conversation Archive (*.gxcv)|*.gxcv|Zip Archive (*.zip)|*.zip",
                DefaultExt = "gxcv",
                FileName = "GxPT-Conversations-" + DateTime.Now.ToString("yyyyMMdd-HHmm") + ".gxcv",
                OverwritePrompt = true
            })
            {
                if (sfd.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    // Do NOT auto-save open tabs; export only conversations already saved on disk
                    if (!Directory.Exists(sourceDir))
                        throw new InvalidOperationException("No conversations folder found to export.");
                    // Optionally, skip export if folder has no conversation files
                    var hasAny = false;
                    try { hasAny = Directory.GetFiles(sourceDir, "*.json").Length > 0; }
                    catch { }
                    if (!hasAny)
                        throw new InvalidOperationException("There are no saved conversations to export.");

                    using (var zip = new ZipFile())
                    {
                        // Use UTF-8 for entry names when needed (replaces obsolete UseUnicodeAsNecessary)
                        zip.AlternateEncoding = Encoding.UTF8;
                        zip.AlternateEncodingUsage = ZipOption.AsNecessary;
                        zip.CompressionLevel = Ionic.Zlib.CompressionLevel.BestCompression;
                        // Add the conversations folder contents at the archive root
                        zip.AddDirectory(sourceDir, "");
                        zip.Save(sfd.FileName);
                    }

                    MessageBox.Show(this, "Export completed.", "Export Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Export failed: " + ex.Message, "Export Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        // Designer wires this
        private void miImport_Click(object sender, EventArgs e)
        {
            using (var ofd = new OpenFileDialog
            {
                Title = "Import Conversations",
                Filter = "GxPT Conversation Archive (*.gxcv)|*.gxcv|Zip Archive (*.zip)|*.zip",
                CheckFileExists = true,
                Multiselect = false
            })
            {
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                if (ConversationStore.ListAll().Count > 0)
                {
                    if (MessageBox.Show(this,
                        "Importing will overwrite existing files with the same names. Continue?",
                        "Import Conversations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    {
                        return;
                    }
                }

                string targetDir = ResolveConversationsFolderPath();
                try
                {
                    Directory.CreateDirectory(targetDir);
                    // Use safe extraction wrapper (guards against zip-slip)
                    ZipSafe.SafeExtract(ofd.FileName, targetDir, true);

                    if (_sidebarManager != null) _sidebarManager.RefreshSidebarList();
                    MessageBox.Show(this, "Import completed.", "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Import failed: " + ex.Message, "Import Conversations", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
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

                // Add placeholder assistant message to stream into and capture its index
                int assistantIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Assistant, string.Empty);
                Logger.Log("Send", "Assistant placeholder index=" + assistantIndex);
                var assistantBuilder = new StringBuilder();

                var modelToUse = GetSelectedModel();

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
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    try { renderTimer.Stop(); renderTimer.Dispose(); }
                                    catch { }
                                    try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                                    catch { }
                                    ctx.Transcript.UpdateMessageAt(assistantIndex, "Error: " + err);
                                    Logger.Log("Send", "Stream error at index=" + assistantIndex + ": " + err);
                                    ctx.IsSending = false;
                                    // don't save on failure; no new assistant content
                                });
                            }
                        );
                    }
                    catch (Exception ex)
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            try { renderTimer.Stop(); renderTimer.Dispose(); }
                            catch { }
                            try { ctx.Transcript.StickToBottomDuringStreaming = false; }
                            catch { }
                            ctx.Transcript.UpdateMessageAt(assistantIndex, "Error: " + ex.Message);
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
                list.Add(new ChatMessage(m.Role, content));
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
                    // Ensure the active context stores whatever is shown
                    var ctx = _tabManager != null ? _tabManager.GetActiveContext() : null;
                    if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                }
            }
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
                if (dlg.ShowDialog(this) == DialogResult.OK)
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
            using (var ofd = new OpenFileDialog())
            {
                ofd.Title = "Attach Text or PDF File(s)";
                ofd.Multiselect = true;
                ofd.Filter = BuildAttachableFilesFilter();
                ofd.CheckFileExists = true;
                if (ofd.ShowDialog(this) != DialogResult.OK) return;

                foreach (var path in ofd.FileNames)
                {
                    string ext = null;
                    try { ext = System.IO.Path.GetExtension(path); }
                    catch { }
                    ext = string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
                    bool isPdf = ext == ".pdf";
                    if (!isPdf && !IsValidTextFile(path))
                    {
                        MessageBox.Show(this, "Skipped non-text file: " + System.IO.Path.GetFileName(path), "Attach File", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        continue;
                    }
                    try
                    {
                        string content;
                        if (isPdf)
                        {
                            content = ExtractTextFromPdf(path);
                            if (string.IsNullOrEmpty(content))
                                content = ""; // still allow empty to attach as placeholder
                        }
                        else
                        {
                            using (var sr = new StreamReader(path, Encoding.UTF8, true))
                                content = sr.ReadToEnd();
                        }
                        ctx.PendingAttachments.Add(new AttachedFile(System.IO.Path.GetFileName(path), content));
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "Failed to read file: " + ex.Message, "Attach File", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
                RebuildAttachmentsBanner();
            }
        }

        // Include PDF in the filter as well as text-based files
        private string BuildAttachableFilesFilter()
        {
            var textFilter = BuildTextFilesFilter();
            // BuildTextFilesFilter returns like: "Text Files|<patterns>|All Files|*.*"
            // Prepend a PDF option for convenience
            return "Supported Files|" + textFilter + ";*.pdf" + "|Text Files|" + textFilter + "|PDF Files|*.pdf" + "|All Files|*.*";
        }

        // Extract text from PDF using iTextSharp 5.x LocationTextExtractionStrategy
        private string ExtractTextFromPdf(string filePath)
        {
            try
            {
                var sb = new StringBuilder(4096);
                using (var reader = new PdfReader(filePath))
                {
                    int n = reader.NumberOfPages;
                    for (int page = 1; page <= n; page++)
                    {
                        Parser.ITextExtractionStrategy strategy = new Parser.LocationTextExtractionStrategy();
                        string pageText = Parser.PdfTextExtractor.GetTextFromPage(reader, page, strategy);
                        pageText = SanitizeDisplayText(pageText);
                        if (!string.IsNullOrEmpty(pageText))
                        {
                            sb.AppendLine(pageText);
                            sb.AppendLine();
                        }
                    }
                }
                return sb.ToString();
            }
            catch (Exception ex)
            {
                try { Logger.Log("PDF", "Failed to extract PDF: " + ex.Message); }
                catch { }
                throw;
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

        private string BuildTextFilesFilter()
        {
            // Cover common text files + all highlighter-related types
            var exts = new List<string>();

            // General text
            exts.AddRange(new[] { "*.txt", "*.md", "*.markdown", "*.log", "*.toml", "*.gitignore", "*.dockerfile", "*.makefile", "*.cmake", "*.diff", "*.patch", "*.csv", "*.sln" });

            // Dockerfiles & Makefiles (support common no-extension names and variants)
            exts.AddRange(new[] { "Dockerfile", "Dockerfile.*" });
            exts.AddRange(new[] { "Makefile", "makefile", "GNUmakefile", "Makefile.*", "makefile.*" });

            // From highlighters
            exts.AddRange(AssemblyHighlighter.FileTypes);
            exts.AddRange(BashHighlighter.FileTypes);
            exts.AddRange(BasicHighlighter.FileTypes);
            exts.AddRange(BatchHighlighter.FileTypes);
            exts.AddRange(CHighlighter.FileTypes);
            exts.AddRange(CppHighlighter.FileTypes);
            exts.AddRange(CSharpHighlighter.FileTypes);
            exts.AddRange(CssHighlighter.FileTypes);
            exts.AddRange(CsvHighlighter.FileTypes);
            exts.AddRange(EbnfHighlighter.FileTypes);
            exts.AddRange(FortranHighlighter.FileTypes);
            exts.AddRange(FSharpHighlighter.FileTypes);
            exts.AddRange(GoHighlighter.FileTypes);
            exts.AddRange(HtmlHighlighter.FileTypes);
            exts.AddRange(JavaHighlighter.FileTypes);
            exts.AddRange(JavaScriptHighlighter.FileTypes);
            exts.AddRange(JsonHighlighter.FileTypes);
            exts.AddRange(LuaHighlighter.FileTypes);
            exts.AddRange(PascalHighlighter.FileTypes);
            exts.AddRange(PerlHighlighter.FileTypes);
            exts.AddRange(PowerShellHighlighter.FileTypes);
            exts.AddRange(PropertiesHighlighter.FileTypes);
            exts.AddRange(PythonHighlighter.FileTypes);
            exts.AddRange(RubyHighlighter.FileTypes);
            exts.AddRange(RegexHighlighter.FileTypes);
            exts.AddRange(RustHighlighter.FileTypes);
            exts.AddRange(SqlHighlighter.FileTypes);
            exts.AddRange(TypeScriptHighlighter.FileTypes);
            exts.AddRange(VisualBasicHighlighter.FileTypes);
            exts.AddRange(XmlHighlighter.FileTypes);
            exts.AddRange(YamlHighlighter.FileTypes);
            exts.AddRange(ZigHighlighter.FileTypes);

            // Deduplicate
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sb = new StringBuilder();
            for (int i = 0; i < exts.Count; i++)
            {
                string e = exts[i];
                if (string.IsNullOrEmpty(e)) continue;
                if (seen.Contains(e)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append(e);
                seen.Add(e);
            }
            return sb.ToString();
        }

        private bool IsValidTextFile(string filePath)
        {
            try
            {
                var fi = new FileInfo(filePath);
                if (fi.Length > 10 * 1024 * 1024) return false; // 10 MB guard
                int sampleSize = (int)Math.Min(8192, fi.Length);
                byte[] buffer = new byte[sampleSize];
                using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    int read = fs.Read(buffer, 0, buffer.Length);
                    if (read <= 0) return true; // empty is fine
                }
                int nulls = 0, ctrls = 0;
                for (int i = 0; i < buffer.Length; i++)
                {
                    byte b = buffer[i];
                    if (b == 0) nulls++;
                    else if (b < 32 && b != 9 && b != 10 && b != 13) ctrls++;
                }
                double nratio = (double)nulls / buffer.Length;
                double cratio = (double)ctrls / buffer.Length;
                return nratio < 0.01 && cratio < 0.02;
            }
            catch { return false; }
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

                        if (dark)
                        {
                            rtb.BackColor = Color.FromArgb(0x24, 0x27, 0x3A); // Macchiato Base
                            rtb.ForeColor = Color.FromArgb(230, 230, 230);
                        }
                        else
                        {
                            rtb.BackColor = SystemColors.Window;
                            rtb.ForeColor = SystemColors.WindowText;
                        }

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
        private string GetConfiguredDefaultModel()
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
        private void SyncComboModelFromActiveTab()
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

            // Rebuild transcript UI using background parsing and chunked appends to avoid UI freezes
            try
            {
                ctx.Transcript.ClearMessages();
                if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                try { ctx.Transcript.RefreshTheme(); }
                catch { }

                // Snapshot messages to process (skip system for transcript)
                var items = new List<ChatMessage>();
                foreach (var m in convo.History)
                {
                    if (m == null) continue;
                    if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(m);
                }

                // Producer-consumer: parse off-UI, consume on UI timer in small chunks
                var parsed = new List<ParsedMessage>(Math.Max(4, items.Count));
                int produced = 0; // number of parsed items ready in 'parsed'
                int consumed = 0; // number consumed/appended to transcript
                bool parsingDone = false;
                object gate = new object();

                // Start background parsing
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
                                var role = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User;
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
                    finally
                    {
                        parsingDone = true;
                    }
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
                        if (parsingDone)
                        {
                            // Nothing left and parsing finished
                            timer.Stop();
                            timer.Dispose();
                        }
                        return;
                    }

                    // Consume a small batch to keep UI fluid
                    int take = Math.Min(20, avail);
                    ctx.Transcript.BeginBatchUpdates();
                    try
                    {
                        for (int k = 0; k < take; k++)
                        {
                            ParsedMessage msg;
                            lock (gate)
                            {
                                msg = parsed[consumed];
                                consumed++;
                            }
                            if (msg != null)
                                ctx.Transcript.AddParsedMessage(msg.Role, msg.Text, msg.Blocks, msg.Attachments);
                        }
                    }
                    finally
                    {
                        bool last = parsingDone && consumed >= items.Count;
                        bool scrollToBottom = last && !(ctx != null && ctx.NoSaveUntilUserSend);
                        ctx.Transcript.EndBatchUpdates(scrollToBottom);
                        if (last)
                        {
                            // For help templates (no-save until user sends), ensure we land at the top
                            try { if (ctx != null && ctx.NoSaveUntilUserSend && ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                            catch { }
                            timer.Stop();
                            timer.Dispose();
                        }
                    }
                };
                timer.Start();
            }
            catch { }

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

                // Rebuild transcript UI using background parsing and chunked appends
                try
                {
                    ctx.Transcript.ClearMessages();
                    if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                    try { ctx.Transcript.RefreshTheme(); }
                    catch { }

                    var items = new List<ChatMessage>();
                    foreach (var m in convo.History)
                    {
                        if (m == null) continue;
                        if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase)) continue;
                        items.Add(m);
                    }

                    var parsed = new List<ParsedMessage>(Math.Max(4, items.Count));
                    int produced = 0, consumed = 0; bool parsingDone = false; object gate = new object();

                    System.Threading.ThreadPool.QueueUserWorkItem(delegate
                    {
                        try
                        {
                            const int parseChunk = 40;
                            int i = 0;
                            while (i < items.Count)
                            {
                                int count = Math.Min(parseChunk, items.Count - i);
                                var local = new ParsedMessage[count];
                                for (int k = 0; k < count; k++)
                                {
                                    var m2 = items[i + k];
                                    var role = string.Equals(m2.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? MessageRole.Assistant : MessageRole.User;
                                    var text = m2.Content ?? string.Empty;
                                    var blocks = MarkdownParser.ParseMarkdown(text);
                                    var att = (m2.Attachments != null && m2.Attachments.Count > 0) ? new List<AttachedFile>(m2.Attachments) : null;
                                    local[k] = new ParsedMessage { Role = role, Text = text, Blocks = blocks, Attachments = att };
                                }
                                lock (gate) { parsed.AddRange(local); produced += count; }
                                i += count;
                            }
                        }
                        catch { }
                        finally { parsingDone = true; }
                    });

                    var timer = new System.Windows.Forms.Timer();
                    timer.Interval = 15;
                    timer.Tick += (s2, e2) =>
                    {
                        int avail; lock (gate) { avail = produced - consumed; }
                        if (avail <= 0)
                        {
                            if (parsingDone)
                            {
                                timer.Stop(); timer.Dispose();
                            }
                            return;
                        }
                        int take = Math.Min(20, avail);
                        ctx.Transcript.BeginBatchUpdates();
                        try
                        {
                            for (int k = 0; k < take; k++)
                            {
                                ParsedMessage pm; lock (gate) { pm = parsed[consumed]; consumed++; }
                                if (pm != null) ctx.Transcript.AddParsedMessage(pm.Role, pm.Text, pm.Blocks, pm.Attachments);
                            }
                        }
                        finally
                        {
                            bool last = parsingDone && consumed >= items.Count;
                            bool scrollToBottom = last && !(ctx != null && ctx.NoSaveUntilUserSend);
                            ctx.Transcript.EndBatchUpdates(scrollToBottom);
                            if (last)
                            {
                                // For help templates (no-save until user sends), ensure we land at the top
                                try { if (ctx != null && ctx.NoSaveUntilUserSend && ctx.Transcript != null) ctx.Transcript.ScrollToTop(); }
                                catch { }
                                timer.Stop();
                                timer.Dispose();
                            }
                        }
                    };
                    timer.Start();
                }
                catch { }

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
    }
}
