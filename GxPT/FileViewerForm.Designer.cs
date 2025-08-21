namespace GxPT
{
    partial class FileViewerForm
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(FileViewerForm));
            this.rtbFileText = new System.Windows.Forms.RichTextBox();
            this.cmsFileText = new System.Windows.Forms.ContextMenuStrip(this.components);
            this.mnuCopy = new System.Windows.Forms.ToolStripMenuItem();
            this.cmsFileText.SuspendLayout();
            this.SuspendLayout();
            // 
            // rtbFileText
            // 
            this.rtbFileText.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.rtbFileText.ContextMenuStrip = this.cmsFileText;
            this.rtbFileText.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbFileText.Location = new System.Drawing.Point(0, 0);
            this.rtbFileText.Name = "rtbFileText";
            this.rtbFileText.ReadOnly = true;
            this.rtbFileText.Size = new System.Drawing.Size(692, 566);
            this.rtbFileText.TabIndex = 0;
            this.rtbFileText.Text = "";
            // 
            // cmsFileText
            // 
            this.cmsFileText.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.mnuCopy});
            this.cmsFileText.Name = "cmsFileText";
            this.cmsFileText.Size = new System.Drawing.Size(150, 26);
            this.cmsFileText.Opening += new System.ComponentModel.CancelEventHandler(this.cmsFileText_Opening);
            // 
            // mnuCopy
            // 
            this.mnuCopy.Name = "mnuCopy";
            this.mnuCopy.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.C)));
            this.mnuCopy.Size = new System.Drawing.Size(149, 22);
            this.mnuCopy.Text = "Copy";
            this.mnuCopy.Click += new System.EventHandler(this.mnuCopy_Click);
            // 
            // FileViewerForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(692, 566);
            this.Controls.Add(this.rtbFileText);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "FileViewerForm";
            this.Text = "FileViewerForm";
            this.cmsFileText.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.RichTextBox rtbFileText;
        private System.Windows.Forms.ContextMenuStrip cmsFileText;
        private System.Windows.Forms.ToolStripMenuItem mnuCopy;
    }
}