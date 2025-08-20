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
            this.rtbFileText = new System.Windows.Forms.RichTextBox();
            this.SuspendLayout();
            // 
            // rtbFileText
            // 
            this.rtbFileText.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbFileText.Location = new System.Drawing.Point(0, 0);
            this.rtbFileText.Name = "rtbFileText";
            this.rtbFileText.ReadOnly = true;
            this.rtbFileText.Size = new System.Drawing.Size(492, 566);
            this.rtbFileText.TabIndex = 0;
            this.rtbFileText.Text = "";
            // 
            // FileViewerForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(492, 566);
            this.Controls.Add(this.rtbFileText);
            this.Name = "FileViewerForm";
            this.Text = "FileViewerForm";
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.RichTextBox rtbFileText;

    }
}