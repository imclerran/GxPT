using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
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

        // Per-tab chat context so each tab has its own conversation and transcript
        private sealed class ChatTabContext
        {
            public TabPage Page;
            public ChatTranscriptControl Transcript;
            public Conversation Conversation;
            public bool IsSending;
        }

        private readonly Dictionary<TabPage, ChatTabContext> _tabContexts = new Dictionary<TabPage, ChatTabContext>();


        public MainForm()
        {
            InitializeComponent();
            HookEvents();
            InitializeClient();
            if (this.tabControl1 != null)
                this.tabControl1.SelectedIndexChanged += (s, e) => UpdateWindowTitleFromActiveTab();

            // Setup initial tab context for the designer-created tab
            SetupInitialConversationTab();
            // Remove any extra placeholder tab if present in designer
            try { if (this.tabControl1 != null && this.tabPage2 != null) this.tabControl1.TabPages.Remove(this.tabPage2); }
            catch { }
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
                    IsSending = false
                };

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
            page.Controls.Add(transcript);

            var ctx = new ChatTabContext
            {
                Page = page,
                Transcript = transcript,
                Conversation = new Conversation(_client),
                IsSending = false
            };

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

            this.tabControl1.TabPages.Add(page);
            try { this.tabControl1.SelectedTab = page; }
            catch { }
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
                                    try { Logger.Log("Transcript", "Final assistant message at index=" + assistantIndex + ":\n" + (finalText ?? string.Empty)); }
                                    catch { }
                                    Logger.Log("Send", "Assistant finalized at index=" + assistantIndex + ", chars=" + (finalText != null ? finalText.Length : 0));
                                    ctx.IsSending = false;
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
                    this.cmbModel.BeginUpdate();
                    try
                    {
                        this.cmbModel.Items.Clear();
                        foreach (var m in list) this.cmbModel.Items.Add(m);
                        if (!string.IsNullOrEmpty(def)) this.cmbModel.Text = def;
                        else this.cmbModel.SelectedIndex = 0;
                    }
                    finally
                    {
                        this.cmbModel.EndUpdate();
                    }
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
    }
}
