using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.IO;
using System.Windows.Forms;

namespace GxPT
{
    public partial class SettingsForm : Form
    {
        private readonly string _settingsDir;
        private readonly string _settingsFile;

        public SettingsForm()
        {
            InitializeComponent();

            // Compute settings paths under %AppData%\GxPT
            _settingsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GxPT");
            _settingsFile = Path.Combine(_settingsDir, "settings.json");

            // Wire events (in case not hooked up in designer)
            this.Load += SettingsForm_Load;
            this.btnSave.Click += btnSave_Click;

            // Enable Ctrl+S to save settings without closing the form
            this.KeyPreview = true;
            this.KeyDown += SettingsForm_KeyDown;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            try
            {
                EnsureSettingsFileExists();
                // Load JSON content into the editor textbox
                this.textBox1.Text = File.ReadAllText(_settingsFile, Encoding.UTF8);
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

        private void SaveSettingsOnly()
        {
            try
            {
                if (!Directory.Exists(_settingsDir))
                {
                    Directory.CreateDirectory(_settingsDir);
                }

                File.WriteAllText(_settingsFile, this.textBox1.Text ?? string.Empty, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Failed to save settings: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            SaveSettingsOnly();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }

        private void SettingsForm_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.S)
            {
                e.SuppressKeyPress = true; // prevent ding
                SaveSettingsOnly();
                this.Close();
            }
        }
    }
}
