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

namespace GxPT
{
    public partial class MainForm : Form
    {
        private OpenRouterClient _client;
        private const int MinInputHeightPx = 75; // initial and minimum height for input panel
        // future conversation-related helpers can go here

        // Sidebar (Panel1) animation settings
        private const int SidebarMinWidth = 8;
        private const int SidebarMaxWidth = 240;
        private const int SidebarAnimStep = 40; // legacy step (not used with easing)
        private const int SidebarAnimIntervalMs = 10; // timer interval (smoother updates)
        private const int SidebarAnimDurationMs = 100; // total duration for open/close
        private bool _sidebarExpanded; // true when fully expanded
        private bool _sidebarAnimating;
        private int _sidebarTargetWidth; // target width for animation
        private Timer _sidebarTimer;
        private Stopwatch _sidebarAnimWatch = new Stopwatch();
        private int _sidebarStartWidth;

        // Per-tab chat context so each tab has its own conversation and transcript
        private sealed class ChatTabContext
        {
            public TabPage Page;
            public ChatTranscriptControl Transcript;
            public Conversation Conversation;
            public bool IsSending;
            public string SelectedModel; // model selected for this tab
        }

        private readonly Dictionary<TabPage, ChatTabContext> _tabContexts = new Dictionary<TabPage, ChatTabContext>();

        // Context menu for tab strip
        private ContextMenuStrip _tabCtxMenu;
        private ToolStripMenuItem _miTabNew;
        private ToolStripMenuItem _miTabClose;
        private ToolStripMenuItem _miTabCloseOthers;
        private TabPage _tabCtxTarget;
        private GlyphToolStripButton _btnNewTab;
        private GlyphToolStripButton _btnCloseTab;
        private bool _syncingModelCombo; // avoid event feedback loops when syncing combo text
        private ListView _lvConversations; // sidebar list
        private Panel _sidebarArrowPanel; // narrow right strip for the arrow
        private readonly Dictionary<string, TabPage> _openConversationsById = new Dictionary<string, TabPage>();
        // ImageList used to control row height (add vertical padding) for the sidebar list
        private ImageList _lvRowHeightImages;


        public MainForm()
        {
            InitializeComponent();
            HookEvents();
            InitializeClient();
            // Configure split container for sidebar behavior
            try
            {
                if (this.splitContainer1 != null)
                {
                    // Ensure left panel acts as a collapsible sidebar and can't be manually dragged
                    this.splitContainer1.FixedPanel = FixedPanel.Panel1;
                    this.splitContainer1.IsSplitterFixed = true;
                    this.splitContainer1.Panel1MinSize = SidebarMinWidth;
                    this.splitContainer1.Panel2MinSize = 0;
                    this.splitContainer1.SplitterWidth = 1; // thin, unobtrusive divider
                    // Start collapsed
                    this.splitContainer1.SplitterDistance = SidebarMinWidth;
                    _sidebarExpanded = false;

                    // Add sidebar list control and arrow strip
                    EnsureSidebarList();
                    EnsureSidebarArrowStrip();
                    // Keyboard toggle (Enter/Space) accessibility when Panel1 focused
                    this.splitContainer1.Panel1.TabStop = true;
                    this.splitContainer1.Panel1.PreviewKeyDown += Panel1_PreviewKeyDown;
                }
            }
            catch { }

            // Sidebar animation timer
            _sidebarTimer = new Timer();
            _sidebarTimer.Interval = SidebarAnimIntervalMs;
            _sidebarTimer.Tick += SidebarTimer_Tick;
            // Wire menu items and tab events
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
                if (this.closeConversationToolStripMenuItem != null)
                {
                    this.closeConversationToolStripMenuItem.Click -= closeConversationToolStripMenuItem_Click;
                    this.closeConversationToolStripMenuItem.Click += closeConversationToolStripMenuItem_Click;
                }
            }
            catch { }
            // View -> Conversation History toggle wiring and initial checked state
            try
            {
                if (this.miConversationHistory != null)
                {
                    this.miConversationHistory.CheckOnClick = false; // we'll manage Checked strictly from state
                    this.miConversationHistory.Click -= miConversationHistory_Click;
                    this.miConversationHistory.Click += miConversationHistory_Click;
                    UpdateConversationHistoryCheckedState();
                }
            }
            catch { }
            if (this.tabControl1 != null)
            {
                this.tabControl1.SelectedIndexChanged += (s, e) =>
                {
                    UpdateWindowTitleFromActiveTab();
                    SyncComboModelFromActiveTab();
                    // When switching to an existing tab, keep typing flow by focusing the input
                    FocusInputSoon();
                };
                try
                {
                    // Revert to default visuals and dynamic-width tabs
                    this.tabControl1.DrawMode = TabDrawMode.Normal;
                    // No owner-draw; only support middle-click to close a tab
                    this.tabControl1.MouseDown -= tabControl1_MouseDown;
                    this.tabControl1.MouseDown += tabControl1_MouseDown;
                    this.tabControl1.MouseUp -= tabControl1_MouseUp;
                    this.tabControl1.MouseUp += tabControl1_MouseUp;
                }
                catch { }
            }

            // Build tab context menu
            try
            {
                _tabCtxMenu = new ContextMenuStrip();
                _miTabNew = new ToolStripMenuItem("New Tab");
                _miTabClose = new ToolStripMenuItem("Close");
                _miTabCloseOthers = new ToolStripMenuItem("Close Others");

                _miTabNew.Click += delegate { CreateConversationTab(); };
                _miTabClose.Click += delegate { if (_tabCtxTarget != null) CloseConversationTab(_tabCtxTarget); };
                _miTabCloseOthers.Click += delegate { if (_tabCtxTarget != null) CloseOtherTabs(_tabCtxTarget); };

                _tabCtxMenu.Items.AddRange(new ToolStripItem[] { _miTabNew, _miTabClose, _miTabCloseOthers });
            }
            catch { }

            // Add custom + and x buttons to the right side of the MenuStrip without altering existing menu order
            try
            {
                if (this.msMain != null)
                {
                    // Custom-drawn buttons
                    _btnNewTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Plus);
                    _btnNewTab.Margin = new Padding(2, 2, 2, 2);
                    _btnNewTab.Click += delegate { miNewConversation_Click(this, EventArgs.Empty); };
                    _btnNewTab.Alignment = ToolStripItemAlignment.Right; // pin to right edge

                    _btnCloseTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Close);
                    _btnCloseTab.Margin = new Padding(2, 2, 3, 2);
                    _btnCloseTab.Click += delegate { closeConversationToolStripMenuItem_Click(this, EventArgs.Empty); };
                    _btnCloseTab.Alignment = ToolStripItemAlignment.Right; // pin to right edge

                    // Add right-aligned buttons; existing items (File, View) stay in designer order
                    this.msMain.Items.Add(_btnCloseTab);
                    this.msMain.Items.Add(_btnNewTab);
                }
            }
            catch { }

            // Setup initial tab context for the designer-created tab
            SetupInitialConversationTab();
            // Wire banner link and ensure it lays out nicely
            if (this.lnkOpenSettings != null)
                this.lnkOpenSettings.LinkClicked += lnkOpenSettings_LinkClicked;
            if (this.pnlApiKeyBanner != null)
                this.pnlApiKeyBanner.Resize += (s, e) => LayoutApiKeyBanner();
            this.Load += (s, e) => UpdateApiKeyBanner();
            // Ensure baseline sizing and behavior
            if (this.txtMessage != null)
            {
                this.txtMessage.Multiline = true;
                this.txtMessage.AcceptsReturn = true;
                this.txtMessage.ScrollBars = ScrollBars.None;
            }
        }

        private void HookEvents()
        {
            this.txtMessage.KeyDown += txtMessage_KeyDown;
            this.txtMessage.TextChanged += txtMessage_TextChanged;
            this.Resize += MainForm_Resize;
            // Redraw arrow on resize for proper centering and keep list width clear of arrow strip
            try { if (this.splitContainer1 != null) this.splitContainer1.Panel1.Resize += (s, e) => { if (_sidebarArrowPanel != null) _sidebarArrowPanel.Invalidate(); LayoutSidebarChildren(); }; }
            catch { }
            try
            {
                if (this.cmbModel != null)
                {
                    this.cmbModel.SelectedIndexChanged -= cmbModel_SelectedIndexChanged;
                    this.cmbModel.SelectedIndexChanged += cmbModel_SelectedIndexChanged;
                    // Track typed text as well (fires for user edits, not for programmatic changes)
                    this.cmbModel.TextUpdate -= cmbModel_TextUpdate;
                    this.cmbModel.TextUpdate += cmbModel_TextUpdate;
                }
            }
            catch { }
        }

        private string GetSelectedModel()
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
            ApplyFontSizeSettingToAllUi();
        }

        // Build a context for the existing designer tab (tabPage1 + chatTranscript)
        private void SetupInitialConversationTab()
        {
            try
            {
                if (this.tabControl1 == null || this.tabPage1 == null || this.chatTranscript == null)
                    return;

                if (_tabContexts.ContainsKey(this.tabPage1))
                    return;

                var ctx = new ChatTabContext
                {
                    Page = this.tabPage1,
                    Transcript = this.chatTranscript,
                    Conversation = new Conversation(_client),
                    IsSending = false,
                    SelectedModel = GetSelectedModel()
                };
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                EnsureConversationId(ctx);

                // When a name is generated, update the tab text and window title if active
                ctx.Conversation.NameGenerated += delegate(string name)
                {
                    try
                    {
                        if (this.IsHandleCreated)
                        {
                            this.BeginInvoke((MethodInvoker)delegate
                            {
                                ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                                UpdateWindowTitleFromActiveTab();
                            });
                        }
                    }
                    catch { }
                };

                // Initial title
                try { ctx.Page.Text = "New Conversation"; }
                catch { }

                _tabContexts[this.tabPage1] = ctx;
                TrackOpenConversation(ctx);
                // Ensure combo reflects the initial tab's stored model
                SyncComboModelFromActiveTab();
                RefreshSidebarList();
            }
            catch { }
        }

        // Create a new tab with its own transcript + conversation
        private ChatTabContext CreateConversationTab()
        {
            if (this.tabControl1 == null) return null;

            var page = new TabPage("New Conversation");
            page.UseVisualStyleBackColor = true;

            var transcript = new ChatTranscriptControl();
            transcript.Dock = DockStyle.Fill;
            ApplyFontSizeSetting(transcript);
            page.Controls.Add(transcript);

            var ctx = new ChatTabContext
            {
                Page = page,
                Transcript = transcript,
                Conversation = new Conversation(_client),
                IsSending = false,
                SelectedModel = GetSelectedModel()
            };
            ctx.Conversation.SelectedModel = ctx.SelectedModel;
            EnsureConversationId(ctx);

            ctx.Conversation.NameGenerated += delegate(string name)
            {
                try
                {
                    if (this.IsHandleCreated)
                    {
                        this.BeginInvoke((MethodInvoker)delegate
                        {
                            ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                            UpdateWindowTitleFromActiveTab();
                        });
                    }
                }
                catch { }
            };

            _tabContexts[page] = ctx;
            TrackOpenConversation(ctx);

            this.tabControl1.TabPages.Add(page);
            try { this.tabControl1.SelectedTab = page; }
            catch { }
            // After selecting, sync combo to this tab's model
            SyncComboModelFromActiveTab();
            RefreshSidebarList();
            // Ensure input is ready for typing on new tab
            FocusInputSoon();
            return ctx;
        }

        // Get the active tab's chat context
        private ChatTabContext GetActiveContext()
        {
            try
            {
                if (this.tabControl1 == null) return null;
                var page = this.tabControl1.SelectedTab;
                if (page == null) return null;
                ChatTabContext ctx;
                return _tabContexts.TryGetValue(page, out ctx) ? ctx : null;
            }
            catch { return null; }
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

        // Close the active conversation tab
        private void CloseActiveConversationTab()
        {
            try
            {
                if (this.tabControl1 == null) return;
                var page = this.tabControl1.SelectedTab;
                if (page == null) return;
                CloseConversationTab(page);
            }
            catch { }
        }

        // Close a specific conversation tab; if only one tab exists, reset it instead
        private void CloseConversationTab(TabPage page)
        {
            if (page == null) return;

            ChatTabContext ctx;
            _tabContexts.TryGetValue(page, out ctx);

            if (this.tabControl1 != null && this.tabControl1.TabPages.Count <= 1)
            {
                // Reset single remaining tab
                try
                {
                    if (ctx != null)
                    {
                        if (ctx.Transcript != null) ctx.Transcript.ClearMessages();
                        ctx.Conversation = new Conversation(_client);
                        // re-hook name event
                        ctx.Conversation.NameGenerated += delegate(string name)
                        {
                            try
                            {
                                if (this.IsHandleCreated)
                                {
                                    this.BeginInvoke((MethodInvoker)delegate
                                    {
                                        ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                                        UpdateWindowTitleFromActiveTab();
                                    });
                                }
                            }
                            catch { }
                        };
                        page.Text = "New Conversation";
                        UpdateWindowTitleFromActiveTab();
                    }
                }
                catch { }
                return;
            }

            try
            {
                int desiredIndex = -1;
                if (this.tabControl1 != null)
                {
                    try
                    {
                        int idx = this.tabControl1.TabPages.IndexOf(page);
                        if (idx >= 0) desiredIndex = Math.Max(0, idx - 1);
                    }
                    catch { }
                }
                if (_tabContexts.ContainsKey(page)) _tabContexts.Remove(page);
                try
                {
                    // Remove from open map
                    var toRemove = _openConversationsById.Where(kv => object.ReferenceEquals(kv.Value, page)).Select(kv => kv.Key).ToList();
                    foreach (var k in toRemove) _openConversationsById.Remove(k);
                }
                catch { }
                if (this.tabControl1 != null)
                {
                    this.tabControl1.TabPages.Remove(page);
                    // After removal, select the immediate left neighbor if possible
                    try
                    {
                        if (this.tabControl1.TabPages.Count > 0)
                        {
                            if (desiredIndex < 0) desiredIndex = 0;
                            if (desiredIndex >= this.tabControl1.TabPages.Count)
                                desiredIndex = this.tabControl1.TabPages.Count - 1;
                            this.tabControl1.SelectedIndex = desiredIndex;
                        }
                    }
                    catch { }
                }
                try { page.Dispose(); }
                catch { }
                UpdateWindowTitleFromActiveTab();
                RefreshSidebarList();
            }
            catch { }
        }

        private void AddDemoMessages()
        {
            // Add a user message
            chatTranscript.AddMessage(MessageRole.User, "Hello! Can you show me what markdown features you support, including syntax highlighting?");

            // Add an assistant message demonstrating various markdown features
            string assistantMessage = "# Markdown Demo with Syntax Highlighting\n\n" +
                "Hello! I support various **markdown features** including syntax highlighting:\n\n" +
                "## Text Formatting\n" +
                "- **Bold text** using **double asterisks**\n" +
                "- *Italic text* using *single asterisks*\n" +
                "- `Inline code` using backticks\n" +
                "- ***Bold and italic*** combined\n\n" +
                "- Hyperlinks like [Visit OpenAI](https://www.openai.com/) and [GitHub](https://github.com/)\n\n" +
                "## Lists\n" +
                "### Bullet Lists:\n" +
                "- First bullet item\n" +
                "  - Nested bullet (hollow circle)\n" +
                "    - Deeply nested (square)\n" +
                "- Second bullet item\n" +
                "- Third bullet item\n\n" +
                "### Numbered Lists:\n" +
                "1. First numbered item\n" +
                "2. Second numbered item\n" +
                "  1. Nested numbered item\n" +
                "  2. Another nested number\n" +
                "3. Third numbered item\n\n" +
                "## Code Blocks with Syntax Highlighting\n\n" +
                "### C# Example:\n" +
                "```cs\n" +
                "using System;\n\n" +
                "public class HelloWorld\n" +
                "{\n" +
                "    public static void Main(string[] args)\n" +
                "    {\n" +
                "        Console.WriteLine(\"Hello, World!\");\n" +
                "        int number = 42;\n" +
                "        bool isTrue = true;\n" +
                "        // This is a comment\n" +
                "        if (isTrue)\n" +
                "        {\n" +
                "            Console.WriteLine($\"The number is {number}\");\n" +
                "        }\n" +
                "    }\n" +
                "}\n" +
                "```\n\n" +
                "### JavaScript Example:\n" +
                "```js\n" +
                "// JavaScript with syntax highlighting\n" +
                "function greetUser(name) {\n" +
                "    const message = `Hello, ${name}!`;\n" +
                "    console.log(message);\n" +
                "    \n" +
                "    let numbers = [1, 2, 3, 4, 5];\n" +
                "    return numbers.map(n => n * 2);\n" +
                "}\n\n" +
                "const result = greetUser(\"World\");\n" +
                "```\n\n" +
                "### JSON Example:\n" +
                "```json\n" +
                "{\n" +
                "    \"name\": \"John Doe\",\n" +
                "    \"age\": 30,\n" +
                "    \"isActive\": true,\n" +
                "    \"hobbies\": [\"reading\", \"coding\", \"gaming\"]\n" +
                "}\n" +
                "```\n\n" +
                "### Python Example:\n" +
                "```python\n" +
                "# Python code with syntax highlighting\n" +
                "def fibonacci(n):\n" +
                "    if n <= 0:\n" +
                "        return []\n" +
                "    elif n == 1:\n" +
                "        return [0]\n" +
                "    \n" +
                "    sequence = [0, 1]\n" +
                "    while len(sequence) < n:\n" +
                "        next_val = sequence[-1] + sequence[-2]\n" +
                "        sequence.append(next_val)\n" +
                "    \n" +
                "    return sequence\n\n" +
                "result = fibonacci(10)\n" +
                "print('First 10 fibonacci numbers:', result)\n" +
                "```\n\n" +
                "That covers the main features including **syntax highlighting** for C#, JavaScript, JSON, and Python!";

            chatTranscript.AddMessage(MessageRole.Assistant, assistantMessage);

            // Add another message showing unsupported language (should fallback to normal text)
            chatTranscript.AddMessage(MessageRole.User, "What about other languages?");

            string fallbackMessage = "For unsupported languages, the code is displayed as plain text:\n\n" +
                "```rust\n" +
                "// Rust code (not highlighted - shows as plain text)\n" +
                "fn main() {\n" +
                "    println!(\"Hello, world!\");\n" +
                "    let x = 5;\n" +
                "    let y = 10;\n" +
                "    println!(\"x + y = {}\", x + y);\n" +
                "}\n" +
                "```\n\n" +
                "But the core languages (C#, JavaScript, JSON, Python) get full syntax highlighting!";

            chatTranscript.AddMessage(MessageRole.Assistant, fallbackMessage);
        }

        private void miSettings_Click(object sender, EventArgs e)
        {
            using (var dlg = new SettingsForm())
            {
                dlg.ShowDialog(this);
                // Re-init client in case API key changed
                InitializeClient();
                UpdateApiKeyBanner();
            }
        }

        private void miExit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private void btnSend_Click(object sender, EventArgs e)
        {
            var ctx = GetActiveContext();
            if (ctx == null) return;
            // Validate input
            string text = (txtMessage.Text ?? string.Empty).Trim();
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
                ConversationStore.Save(ctx.Conversation); // save when a new user message starts/continues a convo
                Logger.Log("Send", "User message added. HistoryCount=" + ctx.Conversation.History.Count);
                txtMessage.Clear();
                ResetInputBoxHeight();

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
                                    ConversationStore.Save(ctx.Conversation); // save only after streaming completes
                                    try { Logger.Log("Transcript", "Final assistant message at index=" + assistantIndex + ":\n" + (finalText ?? string.Empty)); }
                                    catch { }
                                    Logger.Log("Send", "Assistant finalized at index=" + assistantIndex + ", chars=" + (finalText != null ? finalText.Length : 0));
                                    ctx.IsSending = false;
                                    RefreshSidebarList();
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

        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+A selects all text in the input box
            if (e.Control && e.KeyCode == Keys.A)
            {
                try { if (this.txtMessage != null) this.txtMessage.SelectAll(); }
                catch { }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                // Prevent newline and trigger send
                e.SuppressKeyPress = true;
                e.Handled = true;
                btnSend.PerformClick();
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
                        var act = GetActiveContext();
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
                    var ctx = GetActiveContext();
                    if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                }
            }
            catch { }
        }

        private void txtMessage_TextChanged(object sender, EventArgs e)
        {
            AdjustInputBoxHeight();
        }

        private void MainForm_Resize(object sender, EventArgs e)
        {
            AdjustInputBoxHeight();
        }

        private void ResetInputBoxHeight()
        {
            if (txtMessage == null) return;
            txtMessage.Height = MinInputHeightPx;
            txtMessage.ScrollBars = ScrollBars.None;
        }

        private void AdjustInputBoxHeight()
        {
            if (txtMessage == null) return;

            // Available area: client area minus menu strip height (chatTranscript + input panel share this)
            int menuH = (msMain != null ? msMain.Height : 0);
            int available = Math.Max(0, this.ClientSize.Height - menuH);
            int maxHeight = Math.Max(MinInputHeightPx, (int)Math.Floor(available * 0.5));

            // Measure required height for current text within the textbox width
            int width = Math.Max(10, txtMessage.ClientSize.Width);
            var proposed = new Size(width, int.MaxValue);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            string text = txtMessage.Text;
            if (string.IsNullOrEmpty(text)) text = " ";
            Size measured = TextRenderer.MeasureText(text, txtMessage.Font, proposed, flags);

            // Add a small padding to avoid clipping last line
            int desired = measured.Height + 8;

            // Clamp to [MinInputHeightPx, maxHeight]
            int newHeight = Math.Max(MinInputHeightPx, Math.Min(desired, maxHeight));

            // Apply height to the textbox; panel will auto-size accordingly
            if (txtMessage.Height != newHeight)
                txtMessage.Height = newHeight;

            // Manage scrollbars: only show when content exceeds cap
            bool exceedsCap = desired > maxHeight;
            var newScroll = exceedsCap ? ScrollBars.Vertical : ScrollBars.None;
            if (txtMessage.ScrollBars != newScroll)
                txtMessage.ScrollBars = newScroll;
        }

        // future conversation-related helpers can go here

        // (designer-managed layout)

        // (designer-managed banner)

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
            }
        }

        // File > New Conversation
        private void miNewConversation_Click(object sender, EventArgs e)
        {
            CreateConversationTab();
        }

        // File > Close Conversation
        private void closeConversationToolStripMenuItem_Click(object sender, EventArgs e)
        {
            CloseActiveConversationTab();
        }

        private void tabControl1_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                if (this.tabControl1 == null) return;

                // Middle-click on a tab header closes that tab
                if (e.Button == MouseButtons.Middle)
                {
                    for (int i = 0; i < this.tabControl1.TabPages.Count; i++)
                    {
                        var page = this.tabControl1.TabPages[i];
                        Rectangle r = this.tabControl1.GetTabRect(i);
                        if (r.Contains(e.Location)) { CloseConversationTab(page); return; }
                    }
                    return;
                }

            }
            catch { }
        }

        // Right-click on tab strip to show context menu
        private void tabControl1_MouseUp(object sender, MouseEventArgs e)
        {
            try
            {
                if (this.tabControl1 == null || _tabCtxMenu == null) return;
                if (e.Button != MouseButtons.Right) return;

                // Determine if the click is on a tab header
                _tabCtxTarget = null;
                for (int i = 0; i < this.tabControl1.TabPages.Count; i++)
                {
                    Rectangle r = this.tabControl1.GetTabRect(i);
                    if (r.Contains(e.Location))
                    {
                        _tabCtxTarget = this.tabControl1.TabPages[i];
                        try { this.tabControl1.SelectedTab = _tabCtxTarget; }
                        catch { }
                        break;
                    }
                }

                // Enable/disable items based on target
                bool hasTarget = (_tabCtxTarget != null);
                _miTabClose.Enabled = hasTarget;
                _miTabCloseOthers.Enabled = hasTarget && this.tabControl1.TabPages.Count > 1;

                _tabCtxMenu.Show(this.tabControl1, e.Location);
            }
            catch { }
        }



        // Close all tabs except the provided one; if only one tab exists, no-op
        private void CloseOtherTabs(TabPage keep)
        {
            try
            {
                if (this.tabControl1 == null || keep == null) return;
                // Collect to avoid modifying during enumeration
                var toClose = new List<TabPage>();
                foreach (TabPage p in this.tabControl1.TabPages)
                {
                    if (!object.ReferenceEquals(p, keep)) toClose.Add(p);
                }
                foreach (var p in toClose)
                {
                    CloseConversationTab(p);
                }
            }
            catch { }
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
            }
            catch
            {
                if (this.pnlApiKeyBanner != null) this.pnlApiKeyBanner.Visible = true;
                if (this.txtMessage != null) this.txtMessage.Enabled = false;
                if (this.btnSend != null) this.btnSend.Enabled = false;
                if (this.cmbModel != null) this.cmbModel.Enabled = false;
            }
        }

        // (menu spacer logic removed; using right-aligned ToolStripButtons instead)

        // ===== Sidebar animation and visuals =====
        private void Panel1_ClickToggle(object sender, EventArgs e)
        {
            try
            {
                // If expanded, reduce clickable area to the right half of the arrow strip
                if (_sidebarArrowPanel != null && _sidebarExpanded)
                {
                    var me = e as MouseEventArgs;
                    if (me != null)
                    {
                        int half = _sidebarArrowPanel.Width / 2;
                        if (me.X < half) return; // ignore clicks on left half when expanded
                    }
                }
            }
            catch { }
            ToggleSidebar();
        }

        private void Panel1_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                ToggleSidebar();
                e.IsInputKey = true;
            }
        }

        private void ToggleSidebar()
        {
            if (this.splitContainer1 == null) return;
            if (_sidebarAnimating) return;
            _sidebarStartWidth = this.splitContainer1.SplitterDistance;
            _sidebarTargetWidth = _sidebarExpanded ? SidebarMinWidth : SidebarMaxWidth;
            _sidebarAnimating = true;
            try { _sidebarAnimWatch.Reset(); _sidebarAnimWatch.Start(); }
            catch { }
            _sidebarTimer.Start();
        }

        private void SidebarTimer_Tick(object sender, EventArgs e)
        {
            try
            {
                if (this.splitContainer1 == null) { _sidebarTimer.Stop(); _sidebarAnimating = false; return; }
                if (!_sidebarAnimWatch.IsRunning) { _sidebarAnimWatch.Start(); }

                long elapsed = _sidebarAnimWatch.ElapsedMilliseconds;
                double t = Math.Max(0.0, Math.Min(1.0, (double)elapsed / SidebarAnimDurationMs));
                // Smooth easing to reduce jerkiness
                double eased = EaseInOutCubic(t);

                int start = _sidebarStartWidth;
                int end = _sidebarTargetWidth;
                int next = (int)Math.Round(start + (end - start) * eased);

                int cur = this.splitContainer1.SplitterDistance;
                if (next != cur)
                {
                    this.splitContainer1.SuspendLayout();
                    try { this.splitContainer1.SplitterDistance = next; }
                    finally { this.splitContainer1.ResumeLayout(); }

                    // Invalidate arrow panel only
                    if (_sidebarArrowPanel != null)
                    {
                        int h = _sidebarArrowPanel.ClientSize.Height;
                        var rect = new Rectangle(0, Math.Max(0, h / 2 - 20), _sidebarArrowPanel.Width, 40);
                        _sidebarArrowPanel.Invalidate(rect);
                    }
                    // Ensure list leaves room for the arrow strip while animating
                    LayoutSidebarChildren();
                }

                if (t >= 1.0)
                {
                    // Snap to exact target to avoid off-by-one ambiguity
                    try
                    {
                        this.splitContainer1.SuspendLayout();
                        if (this.splitContainer1.SplitterDistance != _sidebarTargetWidth)
                            this.splitContainer1.SplitterDistance = _sidebarTargetWidth;
                    }
                    finally { this.splitContainer1.ResumeLayout(); }

                    _sidebarTimer.Stop();
                    _sidebarAnimWatch.Stop();
                    _sidebarAnimating = false;
                    // Use the target to set state deterministically
                    _sidebarExpanded = (_sidebarTargetWidth >= SidebarMaxWidth);

                    // Ensure the arrow repaints in its final direction and layout is correct
                    if (_sidebarArrowPanel != null) _sidebarArrowPanel.Invalidate();
                    LayoutSidebarChildren();
                    // Keep View -> Conversation History checked state in sync
                    UpdateConversationHistoryCheckedState();
                    // Re-apply sidebar font/row height so it takes effect immediately after expand/collapse
                    try { ApplyFontSizeSettingToSidebar(); }
                    catch { }
                }
            }
            catch
            {
                try { _sidebarTimer.Stop(); }
                catch { }
                try { _sidebarAnimWatch.Stop(); }
                catch { }
                _sidebarAnimating = false;
            }
        }

        private static double EaseInOutCubic(double t)
        {
            // Cubic easing: accelerate, then decelerate
            return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
        }

        // Draw a very small arrow at the vertical center of the arrow panel.
        // Points right when collapsed (invite to expand), left when expanded (invite to collapse).
        private void Panel1_PaintArrow(object sender, PaintEventArgs e)
        {
            try
            {
                var p = _sidebarArrowPanel ?? (this.splitContainer1 != null ? this.splitContainer1.Panel1 : null);
                if (p == null) return;

                int w = p.ClientSize.Width;
                int h = p.ClientSize.Height;
                if (w <= 0 || h <= 0) return;

                // Decide arrow direction deterministically
                bool pointRight;
                if (_sidebarAnimating)
                {
                    // While animating, point towards the target width
                    pointRight = _sidebarTargetWidth > this.splitContainer1.SplitterDistance;
                }
                else
                {
                    // When idle, reflect the expanded/collapsed state
                    pointRight = !_sidebarExpanded;
                }

                // Arrow size and position (keep it tiny)
                int arrowH = Math.Max(8, Math.Min(12, h / 20));
                int arrowW = Math.Max(5, arrowH / 2 + 2);
                int cy = h / 2;
                int paddingRight = 1; // keep within strip
                int cxRight = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));
                int cxLeft = Math.Max(arrowW + 1, Math.Min(w - paddingRight, w));

                using (var sb = new SolidBrush(Color.DimGray))
                {
                    var oldMode = e.Graphics.SmoothingMode;
                    e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    Point[] tri;
                    if (pointRight)
                    {
                        tri = new[]
                        {
                            new Point(cxRight - arrowW, cy - arrowH/2),
                            new Point(cxRight - arrowW, cy + arrowH/2),
                            new Point(cxRight,            cy)
                        };
                    }
                    else // pointLeft
                    {
                        int cx = cxLeft - 1;
                        tri = new[]
                        {
                            new Point(cx,            cy - arrowH/2),
                            new Point(cx,            cy + arrowH/2),
                            new Point(cx - arrowW,   cy)
                        };
                    }
                    e.Graphics.FillPolygon(sb, tri);
                    e.Graphics.SmoothingMode = oldMode;
                }
            }
            catch { }
        }

        private void EnsureSidebarArrowStrip()
        {
            try
            {
                if (_sidebarArrowPanel != null) return;
                _sidebarArrowPanel = new Panel();
                _sidebarArrowPanel.Width = 14; // slim strip
                _sidebarArrowPanel.Dock = DockStyle.Right;
                _sidebarArrowPanel.Margin = new Padding(0);
                _sidebarArrowPanel.Padding = new Padding(0);
                _sidebarArrowPanel.Cursor = Cursors.Hand;
                _sidebarArrowPanel.BackColor = this.splitContainer1.Panel1.BackColor;
                _sidebarArrowPanel.Paint += Panel1_PaintArrow;
                _sidebarArrowPanel.Click += Panel1_ClickToggle;
                _sidebarArrowPanel.PreviewKeyDown += Panel1_PreviewKeyDown;
                _sidebarArrowPanel.TabStop = true;
                this.splitContainer1.Panel1.Controls.Add(_sidebarArrowPanel);
                _sidebarArrowPanel.BringToFront();
                LayoutSidebarChildren();
            }
            catch { }
        }

        // View -> Conversation History click: toggle sidebar expanded/collapsed
        private void miConversationHistory_Click(object sender, EventArgs e)
        {
            ToggleSidebar();
        }

        // Ensure the menu checked state mirrors the actual expanded state
        private void UpdateConversationHistoryCheckedState()
        {
            try
            {
                if (this.miConversationHistory != null)
                {
                    this.miConversationHistory.Checked = _sidebarExpanded;
                }
            }
            catch { }
        }

        // Custom ToolStripButton with copy-button-like hover/press visuals and +/x glyphs
        private sealed class GlyphToolStripButton : ToolStripButton
        {
            public enum GlyphType { Plus, Close }
            private bool _hover; private bool _pressed; private readonly GlyphType _glyph;
            public GlyphToolStripButton(GlyphType glyph)
            {
                _glyph = glyph;
                DisplayStyle = ToolStripItemDisplayStyle.None;
                AutoSize = false;
                Size = new Size(24, 20);
                Margin = new Padding(2);
            }
            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Rectangle r = new Rectangle(0, 0, (int)this.Width - 1, (int)this.Height - 1);
                if (_hover || _pressed)
                {
                    int shade = _pressed ? 210 : 230;
                    using (var sb = new SolidBrush(Color.FromArgb(shade, shade, shade))) g.FillRectangle(sb, r);
                    using (var pen = new Pen(Color.FromArgb(210, 210, 210))) g.DrawRectangle(pen, r);
                }
                using (var pen = new Pen(Color.FromArgb(80, 80, 80), 2f))
                {
                    int cx = r.Left + r.Width / 2;
                    int cy = r.Top + r.Height / 2;
                    int len = Math.Min(r.Width, r.Height) / 2 - 3;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if (_glyph == GlyphType.Plus)
                    {
                        g.DrawLine(pen, cx - len, cy, cx + len, cy);
                        g.DrawLine(pen, cx, cy - len, cx, cy + len);
                    }
                    else
                    {
                        g.DrawLine(pen, cx - len, cy - len, cx + len, cy + len);
                        g.DrawLine(pen, cx - len, cy + len, cx + len, cy - len);
                    }
                }
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
                var ctx = GetActiveContext();
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
                var ctx = GetActiveContext();
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    // Optional: persist model change only when messages exist
                    if (ctx.Conversation.History.Count > 0)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        RefreshSidebarList();
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
                var ctx = GetActiveContext();
                if (ctx != null) ctx.SelectedModel = GetSelectedModel();
                if (ctx != null && ctx.Conversation != null)
                {
                    ctx.Conversation.SelectedModel = ctx.SelectedModel;
                    if (ctx.Conversation.History.Count > 0)
                    {
                        ConversationStore.Save(ctx.Conversation);
                        RefreshSidebarList();
                    }
                }
            }
            catch { }
        }

        // ===== Sidebar list (Panel1) =====
        private void EnsureSidebarList()
        {
            try
            {
                if (_lvConversations != null) return;
                _lvConversations = new ListView();
                _lvConversations.View = View.Details;
                _lvConversations.FullRowSelect = true;
                _lvConversations.HideSelection = false;
                _lvConversations.HeaderStyle = ColumnHeaderStyle.None;
                _lvConversations.BorderStyle = BorderStyle.None;
                _lvConversations.Dock = DockStyle.Left; // we'll size manually to avoid overlap with arrow strip
                _lvConversations.Columns.Add("Conversation", 200, HorizontalAlignment.Left);
                _lvConversations.MultiSelect = false;
                // Use double-click/Enter to open
                _lvConversations.ItemActivate += LvConversations_ItemActivate;
                // Right-click context menu
                var cms = new ContextMenuStrip();
                var miOpen = new ToolStripMenuItem("Open");
                var miDelete = new ToolStripMenuItem("Delete");
                miOpen.Click += (s, e) => TryOpenSelectedConversation();
                miDelete.Click += (s, e) => DeleteSelectedConversation();
                cms.Items.Add(miOpen);
                cms.Items.Add(miDelete);
                _lvConversations.ContextMenuStrip = cms;

                // Background matches panel for a unified look
                _lvConversations.BackColor = this.splitContainer1.Panel1.BackColor;
                // Use system rendering (no bold)
                _lvConversations.OwnerDraw = false;

                // Increase row height via a 1px-wide SmallImageList with a taller ImageSize
                try
                {
                    if (_lvRowHeightImages == null)
                    {
                        _lvRowHeightImages = new ImageList();
                        int rowHeight = Math.Max(_lvConversations.Font.Height + 8, 22); // add vertical padding
                        _lvRowHeightImages.ImageSize = new Size(1, rowHeight);
                    }
                    _lvConversations.SmallImageList = _lvRowHeightImages;
                }
                catch { }

                // Keep the single column sized correctly on resize and sidebar animation
                _lvConversations.Resize += (s, e) => ResizeSidebarColumn();

                this.splitContainer1.Panel1.Controls.Add(_lvConversations);
                // Apply current font/row height immediately (works even if collapsed)
                try { ApplyFontSizeSettingToSidebar(); }
                catch { }
                RefreshSidebarList();
                LayoutSidebarChildren();
            }
            catch { }
        }

        private void RefreshSidebarList()
        {
            try
            {
                if (_lvConversations == null) return;
                var items = ConversationStore.ListAll();
                _lvConversations.BeginUpdate();
                try
                {
                    _lvConversations.Items.Clear();
                    foreach (var it in items)
                    {
                        string text = string.IsNullOrEmpty(it.Name) ? "New Conversation" : it.Name;
                        var lvi = new ListViewItem(text);
                        lvi.Tag = it; // store ConversationListItem
                        _lvConversations.Items.Add(lvi);
                    }
                    ResizeSidebarColumn();
                }
                finally
                {
                    _lvConversations.EndUpdate();
                }
            }
            catch { }
        }

        // (Owner-draw methods retained but unused when OwnerDraw = false)
        private void LvConversations_DrawItem(object sender, DrawListViewItemEventArgs e) { }
        private void LvConversations_DrawSubItem(object sender, DrawListViewSubItemEventArgs e) { }

        // Ensure the ListView never sits under the arrow strip (prevents scrollbar overlap)
        private void LayoutSidebarChildren()
        {
            try
            {
                if (this.splitContainer1 == null || _lvConversations == null) return;
                int arrowW = (_sidebarArrowPanel != null ? _sidebarArrowPanel.Width : 0);
                int panelW = this.splitContainer1.Panel1.ClientSize.Width;
                int targetW = Math.Max(0, panelW - arrowW);
                if (_lvConversations.Dock != DockStyle.Left) _lvConversations.Dock = DockStyle.Left;
                if (_lvConversations.Width != targetW) _lvConversations.Width = targetW;
            }
            catch { }
        }

        private void LvConversations_ItemActivate(object sender, EventArgs e)
        {
            TryOpenSelectedConversation();
        }

        private void TryOpenSelectedConversation()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.SelectedItems.Count == 0) return;
                var lvi = _lvConversations.SelectedItems[0];
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                // If already open, switch to it
                TabPage page;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out page) && this.tabControl1.TabPages.Contains(page))
                {
                    try { this.tabControl1.SelectedTab = page; }
                    catch { }
                    return;
                }

                // Otherwise, load and open a new tab
                var convo = ConversationStore.Load(_client, info.Path);
                if (convo == null) return;
                // If current tab is blank, reuse it
                var active = GetActiveContext();
                if (active != null && active.Conversation != null && (active.Conversation.History == null || active.Conversation.History.Count == 0))
                {
                    OpenConversationInContext(active, convo);
                }
                else
                {
                    OpenConversationInNewTab(convo);
                }
            }
            catch { }
        }

        private void DeleteSelectedConversation()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.SelectedItems.Count == 0) return;
                var lvi = _lvConversations.SelectedItems[0];
                var info = lvi.Tag as ConversationStore.ConversationListItem;
                if (info == null) return;

                // If open, prevent deleting or prompt to close first
                TabPage openPage;
                if (!string.IsNullOrEmpty(info.Id) && _openConversationsById.TryGetValue(info.Id, out openPage))
                {
                    // Close the tab before deleting
                    CloseConversationTab(openPage);
                }

                ConversationStore.DeletePath(info.Path);
                RefreshSidebarList();
            }
            catch { }
        }

        private void ResizeSidebarColumn()
        {
            try
            {
                if (_lvConversations == null || _lvConversations.Columns.Count == 0) return;
                // Leave space for the arrow strip if present
                int arrowW = (_sidebarArrowPanel != null ? _sidebarArrowPanel.Width : 0);
                int target = Math.Max(20, _lvConversations.ClientSize.Width - arrowW - 2);
                _lvConversations.Columns[0].Width = target;
            }
            catch { }
        }

        private void OpenConversationInNewTab(Conversation convo)
        {
            if (this.tabControl1 == null || convo == null) return;
            var ctx = CreateConversationTab();
            if (ctx == null) return;
            // replace the conversation and refresh transcript UI
            OpenConversationInContext(ctx, convo);
            // Keep focus in input when a new tab opens from history
            FocusInputSoon();
        }

        private void OpenConversationInContext(ChatTabContext ctx, Conversation convo)
        {
            if (ctx == null || convo == null) return;
            // Remove any previous mapping for this page
            try
            {
                var toRemove = _openConversationsById.Where(kv => object.ReferenceEquals(kv.Value, ctx.Page)).Select(kv => kv.Key).ToList();
                foreach (var k in toRemove) _openConversationsById.Remove(k);
            }
            catch { }

            ctx.Conversation = convo;
            ctx.SelectedModel = string.IsNullOrEmpty(convo.SelectedModel) ? GetSelectedModel() : convo.SelectedModel;
            try { this.cmbModel.Text = ctx.SelectedModel; }
            catch { }
            EnsureConversationId(ctx);
            TrackOpenConversation(ctx);

            // Rebuild transcript UI
            try
            {
                ctx.Transcript.ClearMessages();
                ApplyFontSizeSetting(ctx.Transcript);
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

            // If we reused a blank tab or just loaded into a newly created tab, focus input
            FocusInputSoon();
        }

        private void ApplyFontSizeSettingToAllUi()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) { ApplyFontSizeSettingToAllTranscripts(); return; }
                float size = (float)Math.Max(6, Math.Min(48, fs));

                // Core chat transcript(s)
                ApplyFontSizeSettingToAllTranscripts();

                // Input textbox
                try { if (this.txtMessage != null) this.txtMessage.Font = new Font(this.txtMessage.Font.FontFamily, size, this.txtMessage.Font.Style); }
                catch { }
                // Send button
                try { if (this.btnSend != null) this.btnSend.Font = new Font(this.btnSend.Font.FontFamily, size, this.btnSend.Font.Style); }
                catch { }
                // Model combo box
                try { if (this.cmbModel != null) this.cmbModel.Font = new Font(this.cmbModel.Font.FontFamily, size, this.cmbModel.Font.Style); }
                catch { }
                // Sidebar list
                try { ApplyFontSizeSettingToSidebar(); }
                catch { }
                // Tab headers (tab page titles)
                try
                {
                    if (this.tabControl1 != null)
                    {
                        this.tabControl1.Font = new Font(this.tabControl1.Font.FontFamily, size, this.tabControl1.Font.Style);
                        foreach (TabPage p in this.tabControl1.TabPages)
                        {
                            try { if (p != null) p.Font = new Font(p.Font.FontFamily, size, p.Font.Style); }
                            catch { }
                        }
                    }
                }
                catch { }
                // API key banner link/label
                try { if (this.lnkOpenSettings != null) this.lnkOpenSettings.Font = new Font(this.lnkOpenSettings.Font.FontFamily, size, this.lnkOpenSettings.Font.Style); }
                catch { }
                try { if (this.lblNoApiKey != null) this.lblNoApiKey.Font = new Font(this.lblNoApiKey.Font.FontFamily, size, this.lblNoApiKey.Font.Style); }
                catch { }
            }
            catch { }
        }

        private void ApplyFontSizeSettingToAllTranscripts()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                // Designer-created transcript
                try { if (this.chatTranscript != null) this.chatTranscript.Font = new Font(this.chatTranscript.Font.FontFamily, size, this.chatTranscript.Font.Style); }
                catch { }

                // Any transcripts in open tabs
                foreach (var kv in _tabContexts)
                {
                    try
                    {
                        var t = kv.Value.Transcript;
                        if (t != null) t.Font = new Font(t.Font.FontFamily, size, t.Font.Style);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private void ApplyFontSizeSetting(ChatTranscriptControl transcript)
        {
            if (transcript == null) return;
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                transcript.Font = new Font(transcript.Font.FontFamily, size, transcript.Font.Style);
            }
            catch { }
        }

        // Apply font size and row height to the sidebar list, even when collapsed/hidden
        private void ApplyFontSizeSettingToSidebar()
        {
            try
            {
                if (this._lvConversations == null) return;
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                // Update font
                try { this._lvConversations.Font = new Font(this._lvConversations.Font.FontFamily, size, this._lvConversations.Font.Style); }
                catch { }

                // Ensure image list exists and set row height based on font
                if (this._lvRowHeightImages == null)
                    this._lvRowHeightImages = new ImageList();

                int rowHeight = Math.Max(this._lvConversations.Font.Height + 8, 22);
                this._lvRowHeightImages.ImageSize = new Size(1, rowHeight);

                // Force ListView to recalc item height by reassigning the image list
                try
                {
                    var current = this._lvConversations.SmallImageList;
                    this._lvConversations.SmallImageList = null;
                    this._lvConversations.SmallImageList = this._lvRowHeightImages;
                }
                catch { }

                // Adjust column width and refresh
                try { ResizeSidebarColumn(); }
                catch { }
                try { this._lvConversations.Invalidate(); this._lvConversations.Update(); }
                catch { }
            }
            catch { }
        }

        private void TrackOpenConversation(ChatTabContext ctx)
        {
            try
            {
                if (ctx == null || ctx.Conversation == null || string.IsNullOrEmpty(ctx.Conversation.Id)) return;
                _openConversationsById[ctx.Conversation.Id] = ctx.Page;
            }
            catch { }
        }

        private void EnsureConversationId(ChatTabContext ctx)
        {
            try
            {
                if (ctx == null || ctx.Conversation == null) return;
                ConversationStore.EnsureConversationId(ctx.Conversation);
            }
            catch { }
        }

        // Helper: focus the message textbox now (if possible)
        private void FocusInput()
        {
            try
            {
                if (this.txtMessage == null) return;
                if (!this.txtMessage.CanFocus) return;
                this.txtMessage.Focus();
                // place caret at end
                try
                {
                    this.txtMessage.SelectionStart = this.txtMessage.TextLength;
                    this.txtMessage.SelectionLength = 0;
                }
                catch { }
            }
            catch { }
        }

        // Helper: schedule focus on UI queue to occur after layout/selection
        private void FocusInputSoon()
        {
            try { this.BeginInvoke((MethodInvoker)(() => FocusInput())); }
            catch { }
        }

        private void aPIKeysToolStripMenuItem_Click(object sender, EventArgs e)
        {

        }
    }
}
