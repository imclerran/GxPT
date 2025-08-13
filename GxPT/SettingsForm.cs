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
            _settingsFile = Path.Combine(_settingsDir, "settings.yaml");

            // Wire events (in case not hooked up in designer)
            this.Load += SettingsForm_Load;
            this.btnSave.Click += btnSave_Click;
            this.btnApply.Click += btnApply_Click;
        }

        private void SettingsForm_Load(object sender, EventArgs e)
        {
            try
            {
                EnsureSettingsFileExists();
                // Load YAML content into the editor textbox
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
                var defaultYaml = BuildDefaultYaml();
                File.WriteAllText(_settingsFile, defaultYaml, Encoding.UTF8);
            }
        }

        private static string BuildDefaultYaml()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# GxPT Settings");
            sb.AppendLine("");
            sb.AppendLine("# openrouter_api_key: sk-or-v1-xxxxxxxxxxxxxxxxxxxxxxxxxxxx");
            sb.AppendLine("openrouter_api_key: ");
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

        private void btnApply_Click(object sender, EventArgs e)
        {
            SaveSettingsOnly();
        }

        private void btnSave_Click(object sender, EventArgs e)
        {
            SaveSettingsOnly();
            this.DialogResult = DialogResult.OK;
            this.Close();
        }
    }
}
