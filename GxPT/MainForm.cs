using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Diagnostics;
using System.Text;
using System.Windows.Forms;
using GxPT;
using System.IO;
// DotNetZip (Ionic.Zip) v1.12.0.0 for .NET 3.5
using Ionic.Zip;
using System.Reflection;

namespace GxPT
{
    public partial class MainForm : Form
    {
        private OpenRouterClient _client;

        // Manager classes for UI concerns
        private SidebarManager _sidebarManager;
        private TabManager _tabManager;
        private ThemeManager _themeManager;
        private InputManager _inputManager;

        private bool _syncingModelCombo; // avoid event feedback loops when syncing combo text

        public MainForm()
        {
            InitializeComponent();
            InitializeManagers();
            HookEvents();
            InitializeClient();

            // Setup initial tab context for the designer-created tab
            SetupInitialConversationTab();
            _inputManager.SetHintText("Ask anything...");

            // Wire banner link and ensure it lays out nicely
            if (this.lnkOpenSettings != null)
                this.lnkOpenSettings.LinkClicked += lnkOpenSettings_LinkClicked;
            if (this.pnlApiKeyBanner != null)
                this.pnlApiKeyBanner.Resize += (s, e) => LayoutApiKeyBanner();
            this.Load += (s, e) => UpdateApiKeyBanner();
        }

        private void InitializeManagers()
        {
            // Initialize managers for UI concerns
            _sidebarManager = new SidebarManager(this, this.splitContainer1, this.miConversationHistory);
            _tabManager = new TabManager(this, this.tabControl1, this.msMain);
            _themeManager = new ThemeManager(this, this.chatTranscript, this.txtMessage,
                this.btnSend, this.cmbModel, this.lnkOpenSettings, this.lblNoApiKey);
            _inputManager = new InputManager(this, this.txtMessage, this.pnlInput,
                this.btnSend, this.cmbModel, this.splitContainer1, this.pnlApiKeyBanner);

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
        }

        // Event handlers for manager events
        private void OnTabSelected(TabPage selectedTab)
        {
            UpdateWindowTitleFromActiveTab();
            SyncComboModelFromActiveTab();
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
            string curlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "curl.exe");
            _client = new OpenRouterClient(apiKey, curlPath);
            // Populate models from settings and reflect configuration
            PopulateModelsFromSettings();
            UpdateApiKeyBanner();
            if (_themeManager != null) _themeManager.ApplyFontSizeSettingToAllUi();
            // Apply theme across existing transcripts and primary UI
            try { if (_themeManager != null) _themeManager.ApplyThemeToAllTranscripts(); }
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

                if (MessageBox.Show(this,
                    "Importing will overwrite existing files with the same names. Continue?",
                    "Import Conversations", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    return;
                }

                string targetDir = ResolveConversationsFolderPath();
                try
                {
                    Directory.CreateDirectory(targetDir);
                    using (var zip = ZipFile.Read(ofd.FileName, new ReadOptions { Encoding = Encoding.UTF8 }))
                    {
                        foreach (var entry in zip)
                        {
                            entry.Extract(targetDir, ExtractExistingFileAction.OverwriteSilently);
                        }
                    }

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
            string text = _inputManager != null ? (_inputManager.GetInputText() ?? string.Empty) : string.Empty;
            if (text.Length == 0) return;
            if (ctx.IsSending) return; // ensure only one in-flight request per tab

            // Ensure client configured before we lock sending
            if (_client == null || !_client.IsConfigured)
            {
                string reason = _client == null
                    ? "Client not initialized."
                    : (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "curl.exe"))
                        ? "curl.exe not found next to the app."
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
                // Add to UI and history
                ctx.Transcript.AddMessage(MessageRole.User, text);
                ctx.Conversation.AddUserMessage(text);
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                // If this tab was marked as no-save (e.g., help), flip it now and save
                if (ctx.NoSaveUntilUserSend) ctx.NoSaveUntilUserSend = false;
                ConversationStore.Save(ctx.Conversation); // save when a new user message starts/continues a convo
                Logger.Log("Send", "User message added. HistoryCount=" + ctx.Conversation.History.Count);
                if (_inputManager != null) _inputManager.ClearInput();

                // Add placeholder assistant message to stream into and capture its index
                int assistantIndex = ctx.Transcript.AddMessageGetIndex(MessageRole.Assistant, string.Empty);
                Logger.Log("Send", "Assistant placeholder index=" + assistantIndex);
                var assistantBuilder = new StringBuilder();

                var modelToUse = GetSelectedModel();

                // Kick off streaming in background
                System.Threading.ThreadPool.QueueUserWorkItem(delegate
                {
                    try
                    {
                        // Snapshot the history and log it
                        var snapshot = ctx.Conversation.History.ToArray();
                        try
                        {
                            var sbSnap = new StringBuilder();
                            sbSnap.Append("Sending ").Append(snapshot.Length).Append(" messages:\n");
                            for (int i = 0; i < snapshot.Length; i++)
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
                                assistantBuilder.Append(d);
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    ctx.Transcript.UpdateMessageAt(assistantIndex, assistantBuilder.ToString());
                                });
                            },
                            delegate
                            {
                                // finalize on UI thread (update history and unlock send)
                                string finalText = assistantBuilder.ToString();
                                BeginInvoke((MethodInvoker)delegate
                                {
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
                                // Treat as fatal only if the process failed; UI already shows placeholder
                                if (string.IsNullOrEmpty(err)) err = "Unknown error.";
                                BeginInvoke((MethodInvoker)delegate
                                {
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

            // Rebuild transcript UI
            try
            {
                ctx.Transcript.ClearMessages();
                if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                try { ctx.Transcript.RefreshTheme(); }
                catch { }
                foreach (var m in convo.History)
                {
                    if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                        ctx.Transcript.AddMessage(MessageRole.Assistant, m.Content);
                    else if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                        ctx.Transcript.AddMessage(MessageRole.User, m.Content);
                    // skip system in transcript UI
                }
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

                // Rebuild transcript UI
                try
                {
                    ctx.Transcript.ClearMessages();
                    if (_themeManager != null) _themeManager.ApplyFontSetting(ctx.Transcript);
                    try { ctx.Transcript.RefreshTheme(); }
                    catch { }
                    foreach (var m in convo.History)
                    {
                        if (string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase))
                            ctx.Transcript.AddMessage(MessageRole.Assistant, m.Content);
                        else if (string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
                            ctx.Transcript.AddMessage(MessageRole.User, m.Content);
                        // skip system in transcript UI
                    }
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
                // Mark this tab as unsaved until the user sends another message
                ctx.NoSaveUntilUserSend = true;

                // Give the tab a descriptive title and name to bypass auto-naming
                ctx.Page.Text = "API Keys Help";
                if (ctx.Conversation != null)
                {
                    ctx.Conversation.Name = "API Keys Help";
                }

                // Pre-populate messages
                string userMsg1 = "How do I get an API key?";
                ctx.Transcript.AddMessage(MessageRole.User, userMsg1);
                ctx.Conversation.AddUserMessage(userMsg1);

                string assistantMsg1 =
                    "# How to Get an OpenRouter.ai API Key\n\n" +
                    "1. **Create an account at [openrouter.ai](https://openrouter.ai).**\n" +
                    "   - Visit the site and sign up or sign in to access your dashboard.\n" +
                    "2. **(Optional) Add credits.**\n" +
                    "   - If needed, go to your account page and purchase credits to enable model usage.\n" +
                    "3. **Go to the API Keys section.**\n" +
                    "   - Once logged in, locate the section labeled **API Keys** or **Keys** to manage your keys.\n" +
                    "4. **Create a new key and name it.**\n" +
                    "   - Click **Create Key**, enter a descriptive name, and optionally set a credit limit. Save the key immediately—**it won't be visible again** after navigating away.\n" +
                    "---\n\n" +
                    "## Summary\n\n" +
                    "| Step | Action                                                                                         |\n" +
                    "| ---- | ---------------------------------------------------------------------------------------------- |\n" +
                    "| 1    | Create an account at [openrouter.ai](https://openrouter.ai)                                    |\n" +
                    "| 2    | Optionally, add credits                                                                        |\n" +
                    "| 3    | Visit the **API Keys** section                                                                 |\n" +
                    "| 4    | Click **Create Key**, name it, optionally set a credit limit, and **copy the key immediately** |";
                ctx.Transcript.AddMessage(MessageRole.Assistant, assistantMsg1);
                ctx.Conversation.AddAssistantMessage(assistantMsg1);

                string userMsg2 = "How do I set my API key in GxPT?";
                ctx.Transcript.AddMessage(MessageRole.User, userMsg2);
                ctx.Conversation.AddUserMessage(userMsg2);

                string assistantMsg2 =
                    "# How to Set the API key in GxPT\n\n" +
                    "1. **Open settings in GxPT**\n" +
                    "   - Click `File` -> `Settings`\n" +
                    "2. **Paste in your API key from OpenRouter.ai**\n" +
                    "   - Paste the key you generated on OpenRouter into the `OpenRouter API Key` field\n" +
                    "3. **Save your settings**\n" +
                    "   - Click the `Save` button in the bottom right corner of the settings window.\n" +
                    "4. **Happy chatting!**";
                ctx.Transcript.AddMessage(MessageRole.Assistant, assistantMsg2);
                ctx.Conversation.AddAssistantMessage(assistantMsg2);

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
            _inputManager.RemoveHintText("Ask anything...");
            _themeManager.ApplyThemeToTextBox();
        }

        private void txtMessage_Leave(object sender, EventArgs e)
        {
            _inputManager.SetHintText("Ask anything...");
        }
    }
}
