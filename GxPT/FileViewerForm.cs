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
    }
}
