using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization; // .NET 3.5 JSON serializer

namespace GxPT
{
    public partial class SettingsForm : Form
    {
        private readonly string _settingsDir;
        private readonly string _settingsFile;

        // In-memory working copy (unsaved until Save/CTRL+S)
        private SettingsData _working = new SettingsData();

        // Guard to prevent event loops during programmatic sync
        private bool _isSyncing = false;

        // Debounce timer for JSON syntax highlighting
        private Timer _jsonHighlightTimer;

        // Prevent re-entrant highlighting and repeated scheduling during formatting
        private bool _isHighlighting = false;

        // Track pending edited region to highlight (union of ranges until debounce fires)
        private int _pendingHighlightStart = -1;
        private int _pendingHighlightEnd = -1;

        public SettingsForm()
        {
            InitializeComponent();

            // Compute settings paths under %AppData%\GxPT
            _settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            _settingsFile = Path.Combine(_settingsDir, "settings.json");

            // Configure new font size controls
            try
            {
                if (this.lblFontSize != null) this.lblFontSize.Text = "Chat Font Size";
                if (this.nudFontSize != null)
                {
                    this.nudFontSize.DecimalPlaces = 1;
                    this.nudFontSize.Increment = 0.5M;
                    this.nudFontSize.Minimum = 6M;
                    this.nudFontSize.Maximum = 48M;
                }
            }
            catch { }

            // Wire events (in case not hooked up in designer)
            this.Load += SettingsForm_Load;

            // Configure theme controls (created in Designer)
            try
            {
                if (this.lblTheme != null) this.lblTheme.Text = "Theme";
                if (this.cmbTheme != null)
                {
                    this.cmbTheme.DropDownStyle = ComboBoxStyle.DropDownList;
                    this.cmbTheme.Items.Clear();
                    this.cmbTheme.Items.Add("light");
                    this.cmbTheme.Items.Add("dark");
                }
            }
            catch { }

            // Enable Ctrl+S to save settings without closing the form
            this.KeyPreview = true;
            this.KeyDown += SettingsForm_KeyDown;

            // Keep tabs in sync
            this.tabControl1.SelectedIndexChanged += TabControl1_SelectedIndexChanged;

            // Keep default model list updated as models are typed
            this.txtModels.TextChanged += TxtModels_TextChanged;

            // JSON editor changed -> debounce highlight
            _jsonHighlightTimer = new Timer();
            _jsonHighlightTimer.Interval = 200; // ms
            _jsonHighlightTimer.Tick += JsonHighlightTimer_Tick;
            this.rtbJson.TextChanged += RtbJson_TextChanged;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            try
            {
                EnsureSettingsFileExists();
                // Load file into working copy, then populate both views
                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                if (!TryDeserialize(raw, out _working))
                {
                    _working = BuildDefaultSettings();
                }

                _isSyncing = true;
                try
                {
                    ApplySettingsToVisualControls(_working);
                    UpdateJsonEditorFromSettings(_working);
                }
                finally { _isSyncing = false; }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to load settings: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void EnsureSettingsFileExists()
        {
            if (!Directory.Exists(_settingsDir))
            {
                Directory.CreateDirectory(_settingsDir);
            }

            if (!File.Exists(_settingsFile))
            {
                var defaultJson = BuildDefaultJson();
                File.WriteAllText(_settingsFile, defaultJson, Encoding.UTF8);
            }
        }

        private static string BuildDefaultJson()
        {
            // defaults: empty key, sensible model list, default model, and font size from chat's default
            float defaultFontSize = GetChatDefaultFontSize();
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"openrouter_api_key\": \"\",");
            sb.AppendLine("  \"models\": [");
            sb.AppendLine("    \"anthropic/claude-3.7-sonnet\",");
            sb.AppendLine("    \"anthropic/claude-sonnet-4\",");
            sb.AppendLine("    \"google/gemini-2.5-flash\",");
            sb.AppendLine("    \"google/gemini-2.5-pro\",");
            sb.AppendLine("    \"openai/gpt-4o\",");
            sb.AppendLine("    \"openai/gpt-5-chat\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"default_model\": \"openai/gpt-4o\",");
            sb.AppendLine("  \"theme\": \"light\",");
            sb.AppendLine("  \"font_size\": " + defaultFontSize.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",");
            sb.AppendLine("  \"enable_logging\": false");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static float GetChatDefaultFontSize()
        {
            try
            {
                using (var ctl = new ChatTranscriptControl())
                {
                    var f = ctl.Font;
                    return (f != null ? f.Size : 9f);
                }
            }
            catch { return 9f; }
        }

        // Strongly-typed default object (mirrors BuildDefaultJson)
        private static SettingsData BuildDefaultSettings()
        {
            return new SettingsData
            {
                openrouter_api_key = "",
                models = new List<string>
                {
                    "anthropic/claude-3.7-sonnet",
                    "anthropic/claude-sonnet-4",
                    "google/gemini-2.5-flash",
                    "google/gemini-2.5-pro",
                    "openai/gpt-4o",
                    "openai/gpt-5"
                },
                default_model = "openai/gpt-4o",
                enable_logging = false,
                font_size = GetChatDefaultFontSize(),
                theme = "light"
            };
        }

        private bool SaveSettingsOnly()
        {
            try
            {
                if (!Directory.Exists(_settingsDir))
                {
                    Directory.CreateDirectory(_settingsDir);
                }

                // Ensure working copy reflects active tab before saving
                if (!SyncWorkingSettingsFromActiveTab(true))
                {
                    // If JSON invalid, we already notified the user. Abort save.
                    return false;
                }

                var json = Serialize(_working);
                File.WriteAllText(_settingsFile, json, Encoding.UTF8);

                // Refresh JSON editor with normalized JSON
                _isSyncing = true;
                try { UpdateJsonEditorFromSettings(_working); }
                finally { _isSyncing = false; }

                // If currently on the JSON tab, re-apply highlighting once post-save
                if (this.tabControl1.SelectedTab == this.tabJson)
                {
                    try { BeginInvoke(new Action(HighlightJsonNow)); }
                    catch { /* ignore */ }
                }

                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save settings: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            if (SaveSettingsOnly())
            {
                this.DialogResult = DialogResult.OK;
                this.Close();
            }
        }

        private void SettingsForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true; // prevent ding
                SaveSettingsOnly(); // Save without closing the form
            }
        }

        // Tab synchronization logic
        private void TabControl1_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isSyncing) return;

            bool toJson = this.tabControl1.SelectedTab == this.tabJson;

            _isSyncing = true;
            try
            {
                if (toJson)
                {
                    // Visual -> working -> JSON
                    CaptureVisualControlsToSettings(_working);
                    UpdateJsonEditorFromSettings(_working);
                }
                else
                {
                    // JSON -> working -> Visual
                    SettingsData parsed;
                    string error;
                    if (!TryParseJsonEditorToSettings(out parsed, out error))
                    {
                        _isSyncing = false; // allow tab change
                        var choice = ShowJsonInvalidPrompt(this,
                            "JSON Parse Error",
                            "The JSON is invalid. Reload the last saved settings, or continue editing?",
                            error);
                        _isSyncing = true;

                        if (choice == JsonPromptChoice.Edit)
                        {
                            // Stay on JSON tab
                            this.tabControl1.SelectedTab = this.tabJson;
                            return;
                        }
                        else
                        {
                            // Reload from disk and proceed to Visual tab
                            try
                            {
                                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                                SettingsData loaded;
                                if (!TryDeserialize(raw, out loaded))
                                {
                                    _isSyncing = false;
                                    MessageBox.Show(this, "Could not reload the last saved settings because the file is invalid.",
                                        "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                    _isSyncing = true;
                                    // Stay on JSON tab to edit
                                    this.tabControl1.SelectedTab = this.tabJson;
                                    return;
                                }
                                _working = loaded;
                                ApplySettingsToVisualControls(_working);
                                UpdateJsonEditorFromSettings(_working);
                                // Stay on JSON tab so the user can continue editing there
                                this.tabControl1.SelectedTab = this.tabJson;
                                return;
                            }
                            catch (Exception ex)
                            {
                                _isSyncing = false;
                                MessageBox.Show(this, "Failed to reload settings: " + ex.Message, "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                _isSyncing = true;
                                this.tabControl1.SelectedTab = this.tabJson;
                                return;
                            }
                        }
                    }

                    _working = parsed;
                    ApplySettingsToVisualControls(_working);
                }
            }
            finally { _isSyncing = false; }

            // When switching to JSON tab, run a one-time highlight
            if (toJson)
            {
                try { BeginInvoke(new Action(HighlightJsonNow)); }
                catch { /* ignore */ }
            }
        }

        // --- Serialization helpers (JavaScriptSerializer for .NET 3.5) ---
        private static bool TryDeserialize(string json, out SettingsData settings)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                settings = ser.Deserialize<SettingsData>(json) ?? new SettingsData();
                PostProcess(settings);
                return true;
            }
            catch
            {
                settings = new SettingsData();
                return false;
            }
        }

        private static string Serialize(SettingsData settings)
        {
            var ser = new JavaScriptSerializer();
            return ser.Serialize(settings);
        }

        private void UpdateJsonEditorFromSettings(SettingsData settings)
        {
            var json = Serialize(settings);
            this.rtbJson.Text = PrettyPrintJson(json);
            // Do not trigger highlight here; only on TextChanged
        }

        private bool TryParseJsonEditorToSettings(out SettingsData settings, out string error)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                settings = ser.Deserialize<SettingsData>(this.rtbJson.Text ?? string.Empty) ?? new SettingsData();
                PostProcess(settings);
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                settings = new SettingsData();
                error = ex.Message;
                return false;
            }
        }

        // --- JSON RichTextBox syntax highlighting (debounced) ---
        private void RtbJson_TextChanged(object sender, EventArgs e)
        {
            if (_isSyncing || _isHighlighting) return;

            // Compute an edited range covering the current line +/- one adjacent line
            int caret = this.rtbJson.SelectionStart;
            int selLen = this.rtbJson.SelectionLength;
            int totalLines = this.rtbJson.Lines != null ? this.rtbJson.Lines.Length : 0;

            int startLine = this.rtbJson.GetLineFromCharIndex(Math.Max(0, caret));
            int endLine = this.rtbJson.GetLineFromCharIndex(Math.Max(0, caret + Math.Max(0, selLen - 1)));
            if (totalLines > 0)
            {
                startLine = Math.Max(0, startLine - 1);
                endLine = Math.Min(totalLines - 1, endLine + 1);
            }

            int startPos = this.rtbJson.GetFirstCharIndexFromLine(startLine);
            if (startPos < 0) startPos = 0;
            int nextLinePos = (endLine + 1 < totalLines) ? this.rtbJson.GetFirstCharIndexFromLine(endLine + 1) : this.rtbJson.TextLength;
            int endPos = Math.Max(startPos, nextLinePos);

            // Merge into pending range
            if (_pendingHighlightStart < 0)
            {
                _pendingHighlightStart = startPos;
                _pendingHighlightEnd = endPos;
            }
            else
            {
                _pendingHighlightStart = Math.Min(_pendingHighlightStart, startPos);
                _pendingHighlightEnd = Math.Max(_pendingHighlightEnd, endPos);
            }

            HighlightJsonSoon();
        }

        private void JsonHighlightTimer_Tick(object sender, EventArgs e)
        {
            _jsonHighlightTimer.Stop();
            int start = _pendingHighlightStart;
            int end = _pendingHighlightEnd;
            _pendingHighlightStart = -1;
            _pendingHighlightEnd = -1;
            if (start >= 0 && end >= start)
            {
                HighlightJsonRange(start, end - start);
            }
            else
            {
                HighlightJsonNow();
            }
        }

        private void HighlightJsonSoon()
        {
            if (_jsonHighlightTimer != null)
            {
                _jsonHighlightTimer.Stop();
                _jsonHighlightTimer.Start();
            }
        }

        private void HighlightJsonNow()
        {
            try
            {
                _isHighlighting = true;
                var rtb = this.rtbJson;
                if (rtb == null || rtb.IsDisposed) return;

                string text = rtb.Text ?? string.Empty;
                // Save caret
                int savedStart = rtb.SelectionStart;
                int savedLength = rtb.SelectionLength;

                // Disable redraw
                rtb.SuspendLayout();

                // Reset to default color
                rtb.SelectionStart = 0;
                rtb.SelectionLength = rtb.TextLength;
                rtb.SelectionColor = SystemColors.WindowText;

                if (text.Length > 0)
                {
                    var tokens = SyntaxHighlighter.Highlight("json", text);
                    int maxLen = rtb.TextLength;
                    for (int i = 0; i < tokens.Count; i++)
                    {
                        var t = tokens[i];
                        if (t.Type == TokenType.Normal || t.Length <= 0 || t.StartIndex < 0 || t.StartIndex >= maxLen) continue;

                        int length = t.Length;
                        int end = t.StartIndex + length;
                        if (end > maxLen)
                        {
                            length = Math.Max(0, maxLen - t.StartIndex);
                            if (length == 0) continue;
                        }

                        rtb.SelectionStart = t.StartIndex;
                        rtb.SelectionLength = length;
                        rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                    }
                }

                // Restore caret
                rtb.SelectionStart = Math.Max(0, Math.Min(savedStart, rtb.TextLength));
                rtb.SelectionLength = Math.Max(0, Math.Min(savedLength, rtb.TextLength - rtb.SelectionStart));
            }
            catch
            {
                // ignore
            }
            finally
            {
                this.rtbJson.ResumeLayout();
                this.rtbJson.Invalidate();
                _isHighlighting = false;
            }
        }

        private void HighlightJsonRange(int start, int length)
        {
            if (length <= 0) return;
            try
            {
                _isHighlighting = true;
                var rtb = this.rtbJson;
                if (rtb == null || rtb.IsDisposed) return;

                int maxLen = rtb.TextLength;
                if (start >= maxLen) return;
                if (start + length > maxLen) length = Math.Max(0, maxLen - start);
                if (length == 0) return;

                string segment = (rtb.Text ?? string.Empty).Substring(start, length);

                // Save caret
                int savedStart = rtb.SelectionStart;
                int savedLength = rtb.SelectionLength;

                rtb.SuspendLayout();

                // Reset segment to default color
                rtb.SelectionStart = start;
                rtb.SelectionLength = length;
                rtb.SelectionColor = SystemColors.WindowText;

                var tokens = SyntaxHighlighter.Highlight("json", segment);
                for (int i = 0; i < tokens.Count; i++)
                {
                    var t = tokens[i];
                    if (t.Type == TokenType.Normal || t.Length <= 0) continue;
                    int tStart = start + t.StartIndex;
                    int tLen = t.Length;
                    if (tStart < 0 || tStart >= maxLen) continue;
                    if (tStart + tLen > maxLen)
                    {
                        tLen = Math.Max(0, maxLen - tStart);
                        if (tLen == 0) continue;
                    }

                    rtb.SelectionStart = tStart;
                    rtb.SelectionLength = tLen;
                    rtb.SelectionColor = SyntaxHighlighter.GetTokenColor(t.Type);
                }

                // Restore caret
                rtb.SelectionStart = Math.Max(0, Math.Min(savedStart, rtb.TextLength));
                rtb.SelectionLength = Math.Max(0, Math.Min(savedLength, rtb.TextLength - rtb.SelectionStart));
            }
            catch
            {
                // ignore
            }
            finally
            {
                this.rtbJson.ResumeLayout();
                this.rtbJson.Invalidate();
                _isHighlighting = false;
            }
        }

        // Pretty-print JSON for display in the JSON tab (works on .NET 3.5)
        private static string PrettyPrintJson(string json)
        {
            if (json == null) return string.Empty;
            int indent = 0;
            bool inQuotes = false;
            bool escape = false;
            var sb = new StringBuilder(json.Length * 2);
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];

                if (escape)
                {
                    sb.Append(ch);
                    escape = false;
                    continue;
                }

                if (ch == '\\')
                {
                    sb.Append(ch);
                    if (inQuotes) escape = true;
                    continue;
                }

                if (ch == '"')
                {
                    sb.Append(ch);
                    inQuotes = !inQuotes;
                    continue;
                }

                if (inQuotes)
                {
                    sb.Append(ch);
                    continue;
                }

                switch (ch)
                {
                    case '{':
                    case '[':
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case '}':
                    case ']':
                        sb.Append(Environment.NewLine);
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                        break;
                    case ',':
                        sb.Append(ch);
                        sb.Append(Environment.NewLine);
                        sb.Append(new string(' ', indent * 2));
                        break;
                    case ':':
                        sb.Append(ch);
                        sb.Append(' ');
                        break;
                    default:
                        if (!char.IsWhiteSpace(ch)) sb.Append(ch);
                        break;
                }
            }
            return sb.ToString();
        }

        private static void PostProcess(SettingsData s)
        {
            if (s.models == null) s.models = new List<string>();
            // Trim and de-duplicate models
            var cleaned = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in s.models)
            {
                if (m == null) continue;
                var t = m.Trim();
                if (t.Length == 0) continue;
                if (seen.Add(t)) cleaned.Add(t);
            }
            s.models = cleaned;

            if (string.IsNullOrEmpty(s.default_model))
            {
                s.default_model = s.models.Count > 0 ? s.models[0] : "openai/gpt-4o";
            }

            // Clamp or set default font size
            try
            {
                double fs = s.font_size;
                if (fs <= 0) fs = GetChatDefaultFontSize();
                if (fs < 6) fs = 6; if (fs > 48) fs = 48;
                s.font_size = fs;
            }
            catch { s.font_size = GetChatDefaultFontSize(); }

            // Theme normalization
            try
            {
                string t = s.theme ?? "";
                t = t.Trim().ToLowerInvariant();
                if (t != "dark" && t != "light") t = "light";
                s.theme = t;
            }
            catch { s.theme = "light"; }
        }

        // --- Visual controls <-> working settings ---
        private void ApplySettingsToVisualControls(SettingsData s)
        {
            if (s == null) s = new SettingsData();
            if (s.models == null) s.models = new List<string>();
            // API Key
            this.txtApiKey.Text = s.openrouter_api_key ?? string.Empty;

            // Enable logging
            this.chkEnableLogging.Checked = s.enable_logging;

            // Models list (one per line)
            var lines = (s.models != null && s.models.Count > 0) ? s.models.ToArray() : new string[0];
            this.txtModels.Lines = lines;

            // Default model combobox
            this.cmbDefaultModel.BeginUpdate();
            try
            {
                this.cmbDefaultModel.Items.Clear();
                foreach (var m in s.models) this.cmbDefaultModel.Items.Add(m);

                // Ensure selection
                var def = s.default_model ?? string.Empty;
                if (!string.IsNullOrEmpty(def) && !s.models.Any(x => string.Equals(x, def, StringComparison.OrdinalIgnoreCase)))
                {
                    this.cmbDefaultModel.Items.Add(def);
                }
                this.cmbDefaultModel.SelectedItem = def;
            }
            finally { this.cmbDefaultModel.EndUpdate(); }

            // Font size
            try
            {
                decimal val = (decimal)(s.font_size > 0 ? s.font_size : GetChatDefaultFontSize());
                if (val < this.nudFontSize.Minimum) val = this.nudFontSize.Minimum;
                if (val > this.nudFontSize.Maximum) val = this.nudFontSize.Maximum;
                this.nudFontSize.Value = val;
            }
            catch { }

            // Theme
            try
            {
                string t = s.theme ?? "light";
                if (this.cmbTheme != null)
                {
                    if (!this.cmbTheme.Items.Contains(t))
                    {
                        // ensure items present
                        this.cmbTheme.Items.Clear();
                        this.cmbTheme.Items.Add("light");
                        this.cmbTheme.Items.Add("dark");
                    }
                    this.cmbTheme.SelectedItem = t;
                }
            }
            catch { }
        }

        private void CaptureVisualControlsToSettings(SettingsData target)
        {
            target.openrouter_api_key = this.txtApiKey.Text ?? string.Empty;
            target.enable_logging = this.chkEnableLogging.Checked;

            // Models from multiline textbox
            var models = new List<string>();
            if (this.txtModels.Lines != null)
            {
                foreach (var line in this.txtModels.Lines)
                {
                    if (line == null) continue;
                    var t = line.Trim();
                    if (t.Length > 0 && !models.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))) models.Add(t);
                }
            }
            target.models = models;

            // Default model from combo (SelectedItem preferred, fallback to Text)
            var sel = this.cmbDefaultModel.SelectedItem as string;
            if (string.IsNullOrEmpty(sel)) sel = this.cmbDefaultModel.Text;
            if (string.IsNullOrEmpty(sel)) sel = models.FirstOrDefault() ?? string.Empty;
            target.default_model = sel ?? string.Empty;

            // Font size
            try { target.font_size = (double)this.nudFontSize.Value; }
            catch { target.font_size = GetChatDefaultFontSize(); }

            // Theme
            try
            {
                var themeSel = this.cmbTheme != null ? (this.cmbTheme.SelectedItem as string) : null;
                if (string.IsNullOrEmpty(themeSel) && this.cmbTheme != null) themeSel = this.cmbTheme.Text;
                if (string.IsNullOrEmpty(themeSel)) themeSel = "light";
                target.theme = (themeSel ?? "light").Trim().ToLowerInvariant();
            }
            catch { target.theme = "light"; }
        }

        // When the models textbox changes, add any new non-empty lines to the default model combo box
        private void TxtModels_TextChanged(object sender, EventArgs e)
        {
            if (_isSyncing) return;

            var lines = this.txtModels.Lines;
            // Build unique, trimmed list of models from textbox
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var models = new List<string>();
            if (lines != null)
            {
                foreach (var line in lines)
                {
                    if (line == null) continue;
                    var t = line.Trim();
                    if (t.Length == 0) continue; // ignore empty lines
                    if (unique.Add(t)) models.Add(t);
                }
            }

            // Preserve existing selection if possible
            var prevSelected = this.cmbDefaultModel.SelectedItem as string;

            this.cmbDefaultModel.BeginUpdate();
            try
            {
                this.cmbDefaultModel.Items.Clear();
                foreach (var m in models) this.cmbDefaultModel.Items.Add(m);

                if (!string.IsNullOrEmpty(prevSelected))
                {
                    // Restore selection if it still exists
                    foreach (object item in this.cmbDefaultModel.Items)
                    {
                        var s = item as string;
                        if (s != null && string.Equals(s, prevSelected, StringComparison.OrdinalIgnoreCase))
                        {
                            this.cmbDefaultModel.SelectedItem = s;
                            break;
                        }
                    }
                }
            }
            finally
            {
                this.cmbDefaultModel.EndUpdate();
            }
        }

        // Ensure working copy is current before saving
        private bool SyncWorkingSettingsFromActiveTab(bool showErrors)
        {
            bool jsonActive = this.tabControl1.SelectedTab == this.tabJson;
            if (jsonActive)
            {
                SettingsData parsed;
                string error;
                if (!TryParseJsonEditorToSettings(out parsed, out error))
                {
                    if (showErrors)
                    {
                        var choice = ShowJsonInvalidPrompt(this,
                            "Cannot Save",
                            "The JSON is invalid and cannot be saved. Reload the last saved settings, or continue editing?",
                            error);
                        if (choice == JsonPromptChoice.Reload)
                        {
                            try
                            {
                                var raw = File.ReadAllText(_settingsFile, Encoding.UTF8);
                                SettingsData loaded;
                                if (TryDeserialize(raw, out loaded))
                                {
                                    _working = loaded;
                                    _isSyncing = true;
                                    try
                                    {
                                        // Refresh both views to the last saved state
                                        ApplySettingsToVisualControls(_working);
                                        UpdateJsonEditorFromSettings(_working);
                                    }
                                    finally { _isSyncing = false; }
                                    // Keep user on JSON tab to continue editing
                                    this.tabControl1.SelectedTab = this.tabJson;
                                }
                                else
                                {
                                    MessageBox.Show(this, "Could not reload the last saved settings because the file is invalid.",
                                        "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                                }
                            }
                            catch (Exception ex)
                            {
                                MessageBox.Show(this, "Failed to reload settings: " + ex.Message, "Reload Failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                            }
                        }
                    }
                    // Whether Edit or Reload, do not proceed with save now
                    return false;
                }
                _working = parsed;
            }
            else
            {
                CaptureVisualControlsToSettings(_working);
            }
            return true;
        }

        // --- JSON invalid prompt (Reload/Edit) ---
        private enum JsonPromptChoice { Reload, Edit }

        private static JsonPromptChoice ShowJsonInvalidPrompt(IWin32Window owner, string title, string message, string details)
        {
            using (var dlg = new Form())
            using (var lbl = new Label())
            using (var tb = new TextBox())
            using (var btnReload = new Button())
            using (var btnEdit = new Button())
            {
                dlg.Text = title;
                dlg.StartPosition = FormStartPosition.CenterParent;
                dlg.FormBorderStyle = FormBorderStyle.FixedDialog;
                dlg.MinimizeBox = false;
                dlg.MaximizeBox = false;
                dlg.ClientSize = new Size(600, 320);

                lbl.AutoSize = false;
                lbl.Text = message;
                lbl.SetBounds(12, 12, 576, 40);

                tb.Multiline = true;
                tb.ReadOnly = true;
                tb.ScrollBars = ScrollBars.Vertical;
                tb.SetBounds(12, 60, 576, 200);
                tb.Text = details ?? string.Empty;

                btnReload.Text = "Reload";
                btnReload.SetBounds(412, 270, 80, 28);
                btnReload.DialogResult = DialogResult.Yes;

                btnEdit.Text = "Edit";
                btnEdit.SetBounds(508, 270, 80, 28);
                btnEdit.DialogResult = DialogResult.No;

                // Default is Edit
                dlg.AcceptButton = btnEdit;

                dlg.Controls.AddRange(new Control[] { lbl, tb, btnReload, btnEdit });
                dlg.CancelButton = btnEdit; // ESC = Edit

                var dr = dlg.ShowDialog(owner);
                return dr == DialogResult.Yes ? JsonPromptChoice.Reload : JsonPromptChoice.Edit;
            }
        }

        // Settings schema
        private sealed class SettingsData
        {
            public string openrouter_api_key { get; set; }
            public List<string> models { get; set; }
            public string default_model { get; set; }
            public bool enable_logging { get; set; }
            public double font_size { get; set; }
            public string theme { get; set; }
        }
    }
}
