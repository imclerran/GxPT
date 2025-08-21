using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace GxPT
{
    public partial class FileViewerForm : Form
    {
        public FileViewerForm()
        {
            InitializeComponent();
            ApplyFontSetting();
            ApplyThemeFromSettings();
        }

        private void ApplyFontSetting()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));
                if (this.rtbFileText != null)
                {
                    this.rtbFileText.Font = new System.Drawing.Font(
                        this.rtbFileText.Font.FontFamily,
                        size,
                        this.rtbFileText.Font.Style);
                }
            }
            catch { }
        }

        private void cmsFileText_Opening(object sender, CancelEventArgs e)
        {
            // Enable Copy only when there's a selection
            if (this.mnuCopy != null)
            {
                bool hasSelection = (this.rtbFileText != null) && !string.IsNullOrEmpty(this.rtbFileText.SelectedText);
                this.mnuCopy.Enabled = hasSelection;
            }
        }

        private void mnuCopy_Click(object sender, EventArgs e)
        {
            try
            {
                if (this.rtbFileText != null && !string.IsNullOrEmpty(this.rtbFileText.SelectedText))
                {
                    string sel = this.rtbFileText.SelectedText;
                    // Normalize all newline variants to Windows CRLF to avoid unknown symbols in some editors
                    sel = sel.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine);
                    Clipboard.SetText(sel, TextDataFormat.UnicodeText);
                }
            }
            catch
            {
                // Ignore clipboard exceptions
            }
        }

        private void ApplyThemeFromSettings()
        {
            try
            {
                string theme = null;
                try { theme = AppSettings.GetString("theme"); }
                catch { theme = null; }

                bool dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);

                if (this.rtbFileText != null)
                {
                    if (dark)
                    {
                        // Match ChatTranscriptControl dark app background and text
                        this.rtbFileText.BackColor = System.Drawing.Color.FromArgb(0x24, 0x27, 0x3A);
                        this.rtbFileText.ForeColor = System.Drawing.Color.FromArgb(230, 230, 230);
                    }
                    else
                    {
                        this.rtbFileText.BackColor = System.Drawing.SystemColors.Window;
                        this.rtbFileText.ForeColor = System.Drawing.SystemColors.WindowText;
                    }
                }
            }
            catch { }
        }
    }
}
