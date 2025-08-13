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
        private Conversation _conversation;
        private OpenRouterClient _client;
        private const int MinInputHeightPx = 75; // initial and minimum height for input panel
        // future conversation-related helpers can go here


        public MainForm()
        {
            InitializeComponent();
            HookEvents();
            InitializeClient();
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
            // Read API key from settings.yaml in %AppData%\GxPT
            string apiKey = AppSettings.Get("openrouter_api_key");
            // curl.exe expected next to executable (bin/Debug)
            string curlPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "curl.exe");
            _client = new OpenRouterClient(apiKey, curlPath);
            _conversation = new Conversation(_client);
            _conversation.NameGenerated += delegate(string name)
            {
                try
                {
                    // Ensure UI-thread update
                    if (this.IsHandleCreated)
                        this.BeginInvoke((MethodInvoker)delegate { this.Text = "GxPT - " + name; });
                }
                catch { }
            };
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
            }
        }

        private void miExit_Click(object sender, EventArgs e)
        {
            this.Close();
        }

        private bool _sending;

        private void btnSend_Click(object sender, EventArgs e)
        {
            if (_sending) return;
            string text = (txtMessage.Text ?? string.Empty).Trim();
            if (text.Length == 0) return;

            // Add to UI and history
            chatTranscript.AddMessage(MessageRole.User, text);
            _conversation.AddUserMessage(text);
            txtMessage.Clear();
            ResetInputBoxHeight();

            // If this is the first message, request a conversation name in the background
            if (_conversation.History.Count == 1)
            {
                _conversation.EnsureNameGenerated(text);
            }

            // Add placeholder assistant message to stream into
            chatTranscript.AddMessage(MessageRole.Assistant, "");
            var assistantBuilder = new StringBuilder();

            if (_client == null || !_client.IsConfigured)
            {
                string reason = _client == null ? "Client not initialized." : (!System.IO.File.Exists(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "curl.exe")) ? "curl.exe not found next to the app." : "Missing API key in settings.yaml.");
                chatTranscript.UpdateLastMessage("Error: " + reason);
                return;
            }

            var modelToUse = GetSelectedModel();

            _sending = true;

            // Kick off streaming in background
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    // Capture the model selection at send time
                    //var modelToUse = GetSelectedModel();
                    _client.CreateCompletionStream(
                        modelToUse,
                        _conversation.History.ToArray(),
                        delegate(string d)
                        {
                            assistantBuilder.Append(d);
                            BeginInvoke((MethodInvoker)delegate { chatTranscript.UpdateLastMessage(assistantBuilder.ToString()); });
                        },
                        delegate
                        {
                            // finalize
                            string finalText = assistantBuilder.ToString();
                            _conversation.AddAssistantMessage(finalText);
                            BeginInvoke((MethodInvoker)delegate { _sending = false; });
                        },
                        delegate(string err)
                        {
                            if (string.IsNullOrEmpty(err)) return;
                            BeginInvoke((MethodInvoker)delegate { chatTranscript.UpdateLastMessage("Error: " + err); _sending = false; });
                        }
                    );
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate { chatTranscript.UpdateLastMessage("Error: " + ex.Message); _sending = false; });
                }
            });
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
    }
}
