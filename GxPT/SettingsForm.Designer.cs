namespace GxPT
{
    partial class SettingsForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(SettingsForm));
            this.flowLayoutPanel1 = new System.Windows.Forms.FlowLayoutPanel();
            this.btnSave = new System.Windows.Forms.Button();
            this.rtbJson = new System.Windows.Forms.RichTextBox();
            this.tblSettings = new System.Windows.Forms.TableLayoutPanel();
            this.lblApiKey = new System.Windows.Forms.Label();
            this.lblModels = new System.Windows.Forms.Label();
            this.lblDefaultModel = new System.Windows.Forms.Label();
            this.lblFontSize = new System.Windows.Forms.Label();
            this.lblTheme = new System.Windows.Forms.Label();
            this.lblEnableLogging = new System.Windows.Forms.Label();
            this.txtApiKey = new System.Windows.Forms.TextBox();
            this.txtModels = new System.Windows.Forms.TextBox();
            this.cmbDefaultModel = new System.Windows.Forms.ComboBox();
            this.nudFontSize = new System.Windows.Forms.NumericUpDown();
            this.cmbTheme = new System.Windows.Forms.ComboBox();
            this.chkEnableLogging = new System.Windows.Forms.CheckBox();
            this.lblTranscriptMaxWidth = new System.Windows.Forms.Label();
            this.nudTranscriptMaxWidth = new System.Windows.Forms.NumericUpDown();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabVisual = new System.Windows.Forms.TabPage();
            this.tabJson = new System.Windows.Forms.TabPage();
            this.lblMessageMaxWidth = new System.Windows.Forms.Label();
            this.nudMessageMaxWidth = new System.Windows.Forms.NumericUpDown();
            this.flowLayoutPanel1.SuspendLayout();
            this.tblSettings.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudFontSize)).BeginInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudTranscriptMaxWidth)).BeginInit();
            this.tabControl1.SuspendLayout();
            this.tabVisual.SuspendLayout();
            this.tabJson.SuspendLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudMessageMaxWidth)).BeginInit();
            this.SuspendLayout();
            // 
            // flowLayoutPanel1
            // 
            this.flowLayoutPanel1.AutoSize = true;
            this.flowLayoutPanel1.Controls.Add(this.btnSave);
            this.flowLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.flowLayoutPanel1.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.flowLayoutPanel1.Location = new System.Drawing.Point(0, 389);
            this.flowLayoutPanel1.Name = "flowLayoutPanel1";
            this.flowLayoutPanel1.Size = new System.Drawing.Size(604, 23);
            this.flowLayoutPanel1.TabIndex = 0;
            // 
            // btnSave
            // 
            this.btnSave.Location = new System.Drawing.Point(529, 0);
            this.btnSave.Margin = new System.Windows.Forms.Padding(0);
            this.btnSave.Name = "btnSave";
            this.btnSave.Size = new System.Drawing.Size(75, 23);
            this.btnSave.TabIndex = 0;
            this.btnSave.Text = "Save";
            this.btnSave.UseVisualStyleBackColor = true;
            this.btnSave.Click += new System.EventHandler(this.btnSave_Click);
            // 
            // rtbJson
            // 
            this.rtbJson.AcceptsTab = true;
            this.rtbJson.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.rtbJson.DetectUrls = false;
            this.rtbJson.Dock = System.Windows.Forms.DockStyle.Fill;
            this.rtbJson.Font = new System.Drawing.Font("Consolas", 9F);
            this.rtbJson.HideSelection = false;
            this.rtbJson.Location = new System.Drawing.Point(3, 3);
            this.rtbJson.Name = "rtbJson";
            this.rtbJson.ScrollBars = System.Windows.Forms.RichTextBoxScrollBars.ForcedBoth;
            this.rtbJson.Size = new System.Drawing.Size(578, 311);
            this.rtbJson.TabIndex = 1;
            this.rtbJson.Text = "";
            this.rtbJson.WordWrap = false;
            // 
            // tblSettings
            // 
            this.tblSettings.ColumnCount = 2;
            this.tblSettings.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tblSettings.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblSettings.Controls.Add(this.lblApiKey, 0, 0);
            this.tblSettings.Controls.Add(this.lblModels, 0, 1);
            this.tblSettings.Controls.Add(this.lblDefaultModel, 0, 2);
            this.tblSettings.Controls.Add(this.lblTheme, 0, 3);
            this.tblSettings.Controls.Add(this.lblTranscriptMaxWidth, 0, 4);
            this.tblSettings.Controls.Add(this.lblMessageMaxWidth, 0, 5);
            this.tblSettings.Controls.Add(this.lblFontSize, 0, 6);
            this.tblSettings.Controls.Add(this.lblEnableLogging, 0, 7);
            this.tblSettings.Controls.Add(this.txtApiKey, 1, 0);
            this.tblSettings.Controls.Add(this.txtModels, 1, 1);
            this.tblSettings.Controls.Add(this.cmbDefaultModel, 1, 2);
            this.tblSettings.Controls.Add(this.cmbTheme, 1, 3);
            this.tblSettings.Controls.Add(this.nudTranscriptMaxWidth, 1, 4);
            this.tblSettings.Controls.Add(this.nudMessageMaxWidth, 1, 5);
            this.tblSettings.Controls.Add(this.nudFontSize, 1, 6);
            this.tblSettings.Controls.Add(this.chkEnableLogging, 1, 7);
            this.tblSettings.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tblSettings.Location = new System.Drawing.Point(3, 3);
            this.tblSettings.Name = "tblSettings";
            this.tblSettings.RowCount = 8;
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Absolute, 20F));
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tblSettings.Size = new System.Drawing.Size(590, 357);
            this.tblSettings.TabIndex = 2;
            // 
            // lblApiKey
            // 
            this.lblApiKey.AutoSize = true;
            this.lblApiKey.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblApiKey.Location = new System.Drawing.Point(3, 0);
            this.lblApiKey.Name = "lblApiKey";
            this.lblApiKey.Size = new System.Drawing.Size(108, 26);
            this.lblApiKey.TabIndex = 0;
            this.lblApiKey.Text = "OpenRouter API Key";
            this.lblApiKey.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblModels
            // 
            this.lblModels.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblModels.AutoSize = true;
            this.lblModels.Location = new System.Drawing.Point(70, 32);
            this.lblModels.Margin = new System.Windows.Forms.Padding(3, 6, 3, 0);
            this.lblModels.Name = "lblModels";
            this.lblModels.Size = new System.Drawing.Size(41, 13);
            this.lblModels.TabIndex = 1;
            this.lblModels.Text = "Models";
            // 
            // lblDefaultModel
            // 
            this.lblDefaultModel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblDefaultModel.Location = new System.Drawing.Point(3, 208);
            this.lblDefaultModel.Name = "lblDefaultModel";
            this.lblDefaultModel.Size = new System.Drawing.Size(108, 27);
            this.lblDefaultModel.TabIndex = 2;
            this.lblDefaultModel.Text = "Default Model";
            this.lblDefaultModel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblFontSize
            // 
            this.lblFontSize.AutoSize = true;
            this.lblFontSize.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblFontSize.Location = new System.Drawing.Point(3, 308);
            this.lblFontSize.Name = "lblFontSize";
            this.lblFontSize.Size = new System.Drawing.Size(108, 26);
            this.lblFontSize.TabIndex = 8;
            this.lblFontSize.Text = "Font Size";
            this.lblFontSize.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblTheme
            // 
            this.lblTheme.AutoSize = true;
            this.lblTheme.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblTheme.Location = new System.Drawing.Point(3, 235);
            this.lblTheme.Name = "lblTheme";
            this.lblTheme.Size = new System.Drawing.Size(108, 27);
            this.lblTheme.TabIndex = 10;
            this.lblTheme.Text = "Theme";
            this.lblTheme.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblEnableLogging
            // 
            this.lblEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblEnableLogging.Location = new System.Drawing.Point(3, 334);
            this.lblEnableLogging.Name = "lblEnableLogging";
            this.lblEnableLogging.Size = new System.Drawing.Size(108, 23);
            this.lblEnableLogging.TabIndex = 3;
            this.lblEnableLogging.Text = "Enable Logging";
            this.lblEnableLogging.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // txtApiKey
            // 
            this.txtApiKey.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtApiKey.Location = new System.Drawing.Point(117, 3);
            this.txtApiKey.Name = "txtApiKey";
            this.txtApiKey.Size = new System.Drawing.Size(470, 20);
            this.txtApiKey.TabIndex = 7;
            // 
            // txtModels
            // 
            this.txtModels.AcceptsReturn = true;
            this.txtModels.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtModels.Location = new System.Drawing.Point(117, 29);
            this.txtModels.Multiline = true;
            this.txtModels.Name = "txtModels";
            this.txtModels.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtModels.Size = new System.Drawing.Size(470, 176);
            this.txtModels.TabIndex = 6;
            // 
            // cmbDefaultModel
            // 
            this.cmbDefaultModel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.cmbDefaultModel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbDefaultModel.DropDownWidth = 175;
            this.cmbDefaultModel.FormattingEnabled = true;
            this.cmbDefaultModel.Location = new System.Drawing.Point(117, 211);
            this.cmbDefaultModel.Name = "cmbDefaultModel";
            this.cmbDefaultModel.Size = new System.Drawing.Size(470, 21);
            this.cmbDefaultModel.TabIndex = 5;
            // 
            // nudFontSize
            // 
            this.nudFontSize.DecimalPlaces = 2;
            this.nudFontSize.Location = new System.Drawing.Point(117, 311);
            this.nudFontSize.Name = "nudFontSize";
            this.nudFontSize.Size = new System.Drawing.Size(120, 20);
            this.nudFontSize.TabIndex = 9;
            // 
            // cmbTheme
            // 
            this.cmbTheme.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbTheme.Location = new System.Drawing.Point(117, 238);
            this.cmbTheme.Name = "cmbTheme";
            this.cmbTheme.Size = new System.Drawing.Size(120, 21);
            this.cmbTheme.TabIndex = 11;
            // 
            // chkEnableLogging
            // 
            this.chkEnableLogging.AutoSize = true;
            this.chkEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkEnableLogging.Location = new System.Drawing.Point(117, 340);
            this.chkEnableLogging.Margin = new System.Windows.Forms.Padding(3, 6, 3, 3);
            this.chkEnableLogging.Name = "chkEnableLogging";
            this.chkEnableLogging.Size = new System.Drawing.Size(470, 14);
            this.chkEnableLogging.TabIndex = 4;
            this.chkEnableLogging.UseVisualStyleBackColor = true;
            // 
            // lblTranscriptMaxWidth
            // 
            this.lblTranscriptMaxWidth.AutoSize = true;
            this.lblTranscriptMaxWidth.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblTranscriptMaxWidth.Location = new System.Drawing.Point(3, 262);
            this.lblTranscriptMaxWidth.Name = "lblTranscriptMaxWidth";
            this.lblTranscriptMaxWidth.Size = new System.Drawing.Size(108, 26);
            this.lblTranscriptMaxWidth.TabIndex = 12;
            this.lblTranscriptMaxWidth.Text = "Transcript Max Width";
            this.lblTranscriptMaxWidth.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // nudTranscriptMaxWidth
            // 
            this.nudTranscriptMaxWidth.Increment = new decimal(new int[] {
            50,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Location = new System.Drawing.Point(117, 265);
            this.nudTranscriptMaxWidth.Maximum = new decimal(new int[] {
            1900,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Minimum = new decimal(new int[] {
            300,
            0,
            0,
            0});
            this.nudTranscriptMaxWidth.Name = "nudTranscriptMaxWidth";
            this.nudTranscriptMaxWidth.Size = new System.Drawing.Size(120, 20);
            this.nudTranscriptMaxWidth.TabIndex = 13;
            this.nudTranscriptMaxWidth.Value = new decimal(new int[] {
            1000,
            0,
            0,
            0});
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabVisual);
            this.tabControl1.Controls.Add(this.tabJson);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(604, 389);
            this.tabControl1.TabIndex = 3;
            // 
            // tabVisual
            // 
            this.tabVisual.Controls.Add(this.tblSettings);
            this.tabVisual.Location = new System.Drawing.Point(4, 22);
            this.tabVisual.Name = "tabVisual";
            this.tabVisual.Padding = new System.Windows.Forms.Padding(3);
            this.tabVisual.Size = new System.Drawing.Size(596, 363);
            this.tabVisual.TabIndex = 0;
            this.tabVisual.Text = "Visual";
            this.tabVisual.UseVisualStyleBackColor = true;
            // 
            // tabJson
            // 
            this.tabJson.Controls.Add(this.rtbJson);
            this.tabJson.Location = new System.Drawing.Point(4, 22);
            this.tabJson.Name = "tabJson";
            this.tabJson.Padding = new System.Windows.Forms.Padding(3);
            this.tabJson.Size = new System.Drawing.Size(584, 317);
            this.tabJson.TabIndex = 1;
            this.tabJson.Text = "JSON";
            this.tabJson.UseVisualStyleBackColor = true;
            // 
            // lblMessageMaxWidth
            // 
            this.lblMessageMaxWidth.AutoSize = true;
            this.lblMessageMaxWidth.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblMessageMaxWidth.Location = new System.Drawing.Point(3, 288);
            this.lblMessageMaxWidth.Name = "lblMessageMaxWidth";
            this.lblMessageMaxWidth.Size = new System.Drawing.Size(108, 20);
            this.lblMessageMaxWidth.TabIndex = 14;
            this.lblMessageMaxWidth.Text = "Message Max Width";
            this.lblMessageMaxWidth.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // nudMessageMaxWidth
            // 
            this.nudMessageMaxWidth.Location = new System.Drawing.Point(117, 291);
            this.nudMessageMaxWidth.Maximum = new decimal(new int[] {
            1900,
            0,
            0,
            0});
            this.nudMessageMaxWidth.Minimum = new decimal(new int[] {
            300,
            0,
            0,
            0});
            this.nudMessageMaxWidth.Name = "nudMessageMaxWidth";
            this.nudMessageMaxWidth.Size = new System.Drawing.Size(120, 20);
            this.nudMessageMaxWidth.TabIndex = 15;
            this.nudMessageMaxWidth.Value = new decimal(new int[] {
            700,
            0,
            0,
            0});
            // 
            // SettingsForm
            // 
            this.AcceptButton = this.btnSave;
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(604, 412);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.flowLayoutPanel1);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Settings";
            this.flowLayoutPanel1.ResumeLayout(false);
            this.tblSettings.ResumeLayout(false);
            this.tblSettings.PerformLayout();
            ((System.ComponentModel.ISupportInitialize)(this.nudFontSize)).EndInit();
            ((System.ComponentModel.ISupportInitialize)(this.nudTranscriptMaxWidth)).EndInit();
            this.tabControl1.ResumeLayout(false);
            this.tabVisual.ResumeLayout(false);
            this.tabJson.ResumeLayout(false);
            ((System.ComponentModel.ISupportInitialize)(this.nudMessageMaxWidth)).EndInit();
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.FlowLayoutPanel flowLayoutPanel1;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.RichTextBox rtbJson;
        private System.Windows.Forms.TableLayoutPanel tblSettings;
        private System.Windows.Forms.Label lblApiKey;
        private System.Windows.Forms.Label lblModels;
        private System.Windows.Forms.Label lblDefaultModel;
        private System.Windows.Forms.Label lblEnableLogging;
        private System.Windows.Forms.CheckBox chkEnableLogging;
        private System.Windows.Forms.ComboBox cmbDefaultModel;
        private System.Windows.Forms.TextBox txtModels;
        private System.Windows.Forms.TextBox txtApiKey;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabVisual;
        private System.Windows.Forms.TabPage tabJson;
        private System.Windows.Forms.Label lblFontSize;
        private System.Windows.Forms.NumericUpDown nudFontSize;
        private System.Windows.Forms.Label lblTheme;
        private System.Windows.Forms.ComboBox cmbTheme;
        private System.Windows.Forms.Label lblTranscriptMaxWidth;
        private System.Windows.Forms.NumericUpDown nudTranscriptMaxWidth;
        private System.Windows.Forms.Label lblMessageMaxWidth;
        private System.Windows.Forms.NumericUpDown nudMessageMaxWidth;
    }
}