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

        public SettingsForm()
        {
            InitializeComponent();

            // Compute settings paths under %AppData%\GxPT
            _settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            _settingsFile = Path.Combine(_settingsDir, "settings.json");

            // Wire events (in case not hooked up in designer)
            this.Load += SettingsForm_Load;

            // Enable Ctrl+S to save settings without closing the form
            this.KeyPreview = true;
            this.KeyDown += SettingsForm_KeyDown;

            // Keep tabs in sync
            this.tabControl1.SelectedIndexChanged += TabControl1_SelectedIndexChanged;

            // Keep default model list updated as models are typed
            this.txtModels.TextChanged += TxtModels_TextChanged;
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
            // defaults: empty key, sensible model list, and default model
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"openrouter_api_key\": \"\",");
            sb.AppendLine("  \"models\": [");
            sb.AppendLine("    \"anthropic/claude-3.7-sonnet\",");
            sb.AppendLine("    \"anthropic/claude-sonnet-4\",");
            sb.AppendLine("    \"google/gemini-2.5-flash\",");
            sb.AppendLine("    \"google/gemini-2.5-pro\",");
            sb.AppendLine("    \"openai/gpt-4o\",");
            sb.AppendLine("    \"openai/gpt-5\"");
            sb.AppendLine("  ],");
            sb.AppendLine("  \"default_model\": \"openai/gpt-4o\",");
            sb.AppendLine("  \"enable_logging\": false");
            sb.AppendLine("}");
            return sb.ToString();
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
                enable_logging = false
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
            this.textBox1.Text = PrettyPrintJson(json);
        }

        private bool TryParseJsonEditorToSettings(out SettingsData settings, out string error)
        {
            try
            {
                var ser = new JavaScriptSerializer();
                settings = ser.Deserialize<SettingsData>(this.textBox1.Text ?? string.Empty) ?? new SettingsData();
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
        }
    }
}
