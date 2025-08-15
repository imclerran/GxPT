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
            this.flowLayoutPanel1 = new System.Windows.Forms.FlowLayoutPanel();
            this.btnSave = new System.Windows.Forms.Button();
            this.rtbJson = new System.Windows.Forms.RichTextBox();
            this.tableLayoutPanel1 = new System.Windows.Forms.TableLayoutPanel();
            this.lblApiKey = new System.Windows.Forms.Label();
            this.lblModels = new System.Windows.Forms.Label();
            this.lblDefaultModel = new System.Windows.Forms.Label();
            this.lblEnableLogging = new System.Windows.Forms.Label();
            this.chkEnableLogging = new System.Windows.Forms.CheckBox();
            this.cmbDefaultModel = new System.Windows.Forms.ComboBox();
            this.txtModels = new System.Windows.Forms.TextBox();
            this.txtApiKey = new System.Windows.Forms.TextBox();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabVisual = new System.Windows.Forms.TabPage();
            this.tabJson = new System.Windows.Forms.TabPage();
            this.flowLayoutPanel1.SuspendLayout();
            this.tableLayoutPanel1.SuspendLayout();
            this.tabControl1.SuspendLayout();
            this.tabVisual.SuspendLayout();
            this.tabJson.SuspendLayout();
            this.SuspendLayout();
            // 
            // flowLayoutPanel1
            // 
            this.flowLayoutPanel1.AutoSize = true;
            this.flowLayoutPanel1.Controls.Add(this.btnSave);
            this.flowLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.flowLayoutPanel1.FlowDirection = System.Windows.Forms.FlowDirection.RightToLeft;
            this.flowLayoutPanel1.Location = new System.Drawing.Point(0, 343);
            this.flowLayoutPanel1.Name = "flowLayoutPanel1";
            this.flowLayoutPanel1.Size = new System.Drawing.Size(592, 23);
            this.flowLayoutPanel1.TabIndex = 0;
            // 
            // btnSave
            // 
            this.btnSave.Location = new System.Drawing.Point(517, 0);
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
            // tableLayoutPanel1
            // 
            this.tableLayoutPanel1.ColumnCount = 2;
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle());
            this.tableLayoutPanel1.ColumnStyles.Add(new System.Windows.Forms.ColumnStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel1.Controls.Add(this.lblApiKey, 0, 0);
            this.tableLayoutPanel1.Controls.Add(this.lblModels, 0, 1);
            this.tableLayoutPanel1.Controls.Add(this.lblDefaultModel, 0, 2);
            this.tableLayoutPanel1.Controls.Add(this.lblEnableLogging, 0, 3);
            this.tableLayoutPanel1.Controls.Add(this.chkEnableLogging, 1, 3);
            this.tableLayoutPanel1.Controls.Add(this.cmbDefaultModel, 1, 2);
            this.tableLayoutPanel1.Controls.Add(this.txtModels, 1, 1);
            this.tableLayoutPanel1.Controls.Add(this.txtApiKey, 1, 0);
            this.tableLayoutPanel1.Dock = System.Windows.Forms.DockStyle.Top;
            this.tableLayoutPanel1.Location = new System.Drawing.Point(3, 3);
            this.tableLayoutPanel1.Name = "tableLayoutPanel1";
            this.tableLayoutPanel1.RowCount = 4;
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle(System.Windows.Forms.SizeType.Percent, 100F));
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.RowStyles.Add(new System.Windows.Forms.RowStyle());
            this.tableLayoutPanel1.Size = new System.Drawing.Size(578, 216);
            this.tableLayoutPanel1.TabIndex = 2;
            // 
            // lblApiKey
            // 
            this.lblApiKey.AutoSize = true;
            this.lblApiKey.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblApiKey.Location = new System.Drawing.Point(3, 0);
            this.lblApiKey.Name = "lblApiKey";
            this.lblApiKey.Size = new System.Drawing.Size(106, 26);
            this.lblApiKey.TabIndex = 0;
            this.lblApiKey.Text = "OpenRouter API Key";
            this.lblApiKey.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblModels
            // 
            this.lblModels.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Right)));
            this.lblModels.AutoSize = true;
            this.lblModels.Location = new System.Drawing.Point(68, 32);
            this.lblModels.Margin = new System.Windows.Forms.Padding(3, 6, 3, 0);
            this.lblModels.Name = "lblModels";
            this.lblModels.Size = new System.Drawing.Size(41, 13);
            this.lblModels.TabIndex = 1;
            this.lblModels.Text = "Models";
            // 
            // lblDefaultModel
            // 
            this.lblDefaultModel.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblDefaultModel.Location = new System.Drawing.Point(3, 166);
            this.lblDefaultModel.Name = "lblDefaultModel";
            this.lblDefaultModel.Size = new System.Drawing.Size(106, 27);
            this.lblDefaultModel.TabIndex = 2;
            this.lblDefaultModel.Text = "Default Model";
            this.lblDefaultModel.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // lblEnableLogging
            // 
            this.lblEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.lblEnableLogging.Location = new System.Drawing.Point(3, 193);
            this.lblEnableLogging.Name = "lblEnableLogging";
            this.lblEnableLogging.Size = new System.Drawing.Size(106, 23);
            this.lblEnableLogging.TabIndex = 3;
            this.lblEnableLogging.Text = "Enable Logging";
            this.lblEnableLogging.TextAlign = System.Drawing.ContentAlignment.MiddleRight;
            // 
            // chkEnableLogging
            // 
            this.chkEnableLogging.AutoSize = true;
            this.chkEnableLogging.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chkEnableLogging.Location = new System.Drawing.Point(115, 199);
            this.chkEnableLogging.Margin = new System.Windows.Forms.Padding(3, 6, 3, 3);
            this.chkEnableLogging.Name = "chkEnableLogging";
            this.chkEnableLogging.Size = new System.Drawing.Size(460, 14);
            this.chkEnableLogging.TabIndex = 4;
            this.chkEnableLogging.UseVisualStyleBackColor = true;
            // 
            // cmbDefaultModel
            // 
            this.cmbDefaultModel.FormattingEnabled = true;
            this.cmbDefaultModel.Location = new System.Drawing.Point(115, 169);
            this.cmbDefaultModel.Name = "cmbDefaultModel";
            this.cmbDefaultModel.Size = new System.Drawing.Size(121, 21);
            this.cmbDefaultModel.TabIndex = 5;
            // 
            // txtModels
            // 
            this.txtModels.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtModels.Location = new System.Drawing.Point(115, 29);
            this.txtModels.Multiline = true;
            this.txtModels.Name = "txtModels";
            this.txtModels.ScrollBars = System.Windows.Forms.ScrollBars.Vertical;
            this.txtModels.Size = new System.Drawing.Size(460, 134);
            this.txtModels.TabIndex = 6;
            // 
            // txtApiKey
            // 
            this.txtApiKey.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left)
                        | System.Windows.Forms.AnchorStyles.Right)));
            this.txtApiKey.Location = new System.Drawing.Point(115, 3);
            this.txtApiKey.Name = "txtApiKey";
            this.txtApiKey.Size = new System.Drawing.Size(460, 20);
            this.txtApiKey.TabIndex = 7;
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabVisual);
            this.tabControl1.Controls.Add(this.tabJson);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(592, 343);
            this.tabControl1.TabIndex = 3;
            // 
            // tabVisual
            // 
            this.tabVisual.Controls.Add(this.tableLayoutPanel1);
            this.tabVisual.Location = new System.Drawing.Point(4, 22);
            this.tabVisual.Name = "tabVisual";
            this.tabVisual.Padding = new System.Windows.Forms.Padding(3);
            this.tabVisual.Size = new System.Drawing.Size(584, 317);
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
            // SettingsForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(592, 366);
            this.Controls.Add(this.tabControl1);
            this.Controls.Add(this.flowLayoutPanel1);
            this.Name = "SettingsForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "SettingsForm";
            this.flowLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.ResumeLayout(false);
            this.tableLayoutPanel1.PerformLayout();
            this.tabControl1.ResumeLayout(false);
            this.tabVisual.ResumeLayout(false);
            this.tabJson.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.FlowLayoutPanel flowLayoutPanel1;
        private System.Windows.Forms.Button btnSave;
        private System.Windows.Forms.RichTextBox rtbJson;
        private System.Windows.Forms.TableLayoutPanel tableLayoutPanel1;
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
    }
}