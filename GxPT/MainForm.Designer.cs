namespace GxPT
{
    partial class MainForm
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainForm));
            this.msMain = new System.Windows.Forms.MenuStrip();
            this.miFile = new System.Windows.Forms.ToolStripMenuItem();
            this.miSettings = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator2 = new System.Windows.Forms.ToolStripSeparator();
            this.miNewConversation = new System.Windows.Forms.ToolStripMenuItem();
            this.closeConversationToolStripMenuItem = new System.Windows.Forms.ToolStripMenuItem();
            this.toolStripSeparator1 = new System.Windows.Forms.ToolStripSeparator();
            this.miExit = new System.Windows.Forms.ToolStripMenuItem();
            this.miView = new System.Windows.Forms.ToolStripMenuItem();
            this.miConversationHistory = new System.Windows.Forms.ToolStripMenuItem();
            this.pnlInput = new System.Windows.Forms.Panel();
            this.txtMessage = new System.Windows.Forms.TextBox();
            this.pnlInputRight = new System.Windows.Forms.Panel();
            this.btnSend = new System.Windows.Forms.Button();
            this.cmbModel = new System.Windows.Forms.ComboBox();
            this.pnlBottom = new System.Windows.Forms.Panel();
            this.pnlApiKeyBanner = new System.Windows.Forms.Panel();
            this.lnkOpenSettings = new System.Windows.Forms.LinkLabel();
            this.lblNoApiKey = new System.Windows.Forms.Label();
            this.tabControl1 = new System.Windows.Forms.TabControl();
            this.tabPage1 = new System.Windows.Forms.TabPage();
            this.splitContainer1 = new System.Windows.Forms.SplitContainer();
            this.chatTranscript = new GxPT.ChatTranscriptControl();
            this.miHelp = new System.Windows.Forms.ToolStripMenuItem();
            this.miApiKeysHelp = new System.Windows.Forms.ToolStripMenuItem();
            this.msMain.SuspendLayout();
            this.pnlInput.SuspendLayout();
            this.pnlInputRight.SuspendLayout();
            this.pnlBottom.SuspendLayout();
            this.pnlApiKeyBanner.SuspendLayout();
            this.tabControl1.SuspendLayout();
            this.tabPage1.SuspendLayout();
            this.splitContainer1.Panel2.SuspendLayout();
            this.splitContainer1.SuspendLayout();
            this.SuspendLayout();
            // 
            // msMain
            // 
            this.msMain.Items.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miFile,
            this.miView,
            this.miHelp});
            this.msMain.Location = new System.Drawing.Point(0, 0);
            this.msMain.Name = "msMain";
            this.msMain.Size = new System.Drawing.Size(892, 24);
            this.msMain.TabIndex = 1;
            this.msMain.Text = "menuStrip1";
            // 
            // miFile
            // 
            this.miFile.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miSettings,
            this.toolStripSeparator2,
            this.miNewConversation,
            this.closeConversationToolStripMenuItem,
            this.toolStripSeparator1,
            this.miExit});
            this.miFile.Name = "miFile";
            this.miFile.Size = new System.Drawing.Size(35, 20);
            this.miFile.Text = "&File";
            // 
            // miSettings
            // 
            this.miSettings.Name = "miSettings";
            this.miSettings.Size = new System.Drawing.Size(212, 22);
            this.miSettings.Text = "&Settings";
            this.miSettings.Click += new System.EventHandler(this.miSettings_Click);
            // 
            // toolStripSeparator2
            // 
            this.toolStripSeparator2.Name = "toolStripSeparator2";
            this.toolStripSeparator2.Size = new System.Drawing.Size(209, 6);
            // 
            // miNewConversation
            // 
            this.miNewConversation.Name = "miNewConversation";
            this.miNewConversation.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.N)));
            this.miNewConversation.Size = new System.Drawing.Size(212, 22);
            this.miNewConversation.Text = "&New Conversation";
            this.miNewConversation.Click += new System.EventHandler(this.miNewConversation_Click);
            // 
            // closeConversationToolStripMenuItem
            // 
            this.closeConversationToolStripMenuItem.Name = "closeConversationToolStripMenuItem";
            this.closeConversationToolStripMenuItem.Size = new System.Drawing.Size(212, 22);
            this.closeConversationToolStripMenuItem.Text = "&Close Conversation";
            // 
            // toolStripSeparator1
            // 
            this.toolStripSeparator1.Name = "toolStripSeparator1";
            this.toolStripSeparator1.Size = new System.Drawing.Size(209, 6);
            // 
            // miExit
            // 
            this.miExit.Name = "miExit";
            this.miExit.Size = new System.Drawing.Size(212, 22);
            this.miExit.Text = "E&xit";
            this.miExit.Click += new System.EventHandler(this.miExit_Click);
            // 
            // miView
            // 
            this.miView.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miConversationHistory});
            this.miView.Name = "miView";
            this.miView.Size = new System.Drawing.Size(41, 20);
            this.miView.Text = "&View";
            // 
            // miConversationHistory
            // 
            this.miConversationHistory.Name = "miConversationHistory";
            this.miConversationHistory.ShortcutKeys = ((System.Windows.Forms.Keys)((System.Windows.Forms.Keys.Control | System.Windows.Forms.Keys.H)));
            this.miConversationHistory.Size = new System.Drawing.Size(225, 22);
            this.miConversationHistory.Text = "Conversation &History";
            this.miConversationHistory.Click += new System.EventHandler(this.miConversationHistory_Click);
            // 
            // pnlInput
            // 
            this.pnlInput.AutoSize = true;
            this.pnlInput.Controls.Add(this.txtMessage);
            this.pnlInput.Controls.Add(this.pnlInputRight);
            this.pnlInput.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlInput.Location = new System.Drawing.Point(0, 28);
            this.pnlInput.MinimumSize = new System.Drawing.Size(0, 75);
            this.pnlInput.Name = "pnlInput";
            this.pnlInput.Size = new System.Drawing.Size(885, 75);
            this.pnlInput.TabIndex = 2;
            // 
            // txtMessage
            // 
            this.txtMessage.AcceptsReturn = true;
            this.txtMessage.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtMessage.Location = new System.Drawing.Point(0, 0);
            this.txtMessage.Margin = new System.Windows.Forms.Padding(0);
            this.txtMessage.MaxLength = 0;
            this.txtMessage.Multiline = true;
            this.txtMessage.Name = "txtMessage";
            this.txtMessage.Size = new System.Drawing.Size(710, 75);
            this.txtMessage.TabIndex = 1;
            this.txtMessage.KeyDown += new System.Windows.Forms.KeyEventHandler(this.txtMessage_KeyDown);
            // 
            // pnlInputRight
            // 
            this.pnlInputRight.Controls.Add(this.btnSend);
            this.pnlInputRight.Controls.Add(this.cmbModel);
            this.pnlInputRight.Dock = System.Windows.Forms.DockStyle.Right;
            this.pnlInputRight.Location = new System.Drawing.Point(710, 0);
            this.pnlInputRight.Name = "pnlInputRight";
            this.pnlInputRight.Size = new System.Drawing.Size(175, 75);
            this.pnlInputRight.TabIndex = 3;
            // 
            // btnSend
            // 
            this.btnSend.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.btnSend.Location = new System.Drawing.Point(0, 0);
            this.btnSend.Name = "btnSend";
            this.btnSend.Size = new System.Drawing.Size(175, 54);
            this.btnSend.TabIndex = 0;
            this.btnSend.Text = "Send";
            this.btnSend.UseVisualStyleBackColor = true;
            this.btnSend.Click += new System.EventHandler(this.btnSend_Click);
            // 
            // cmbModel
            // 
            this.cmbModel.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.cmbModel.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.cmbModel.FormattingEnabled = true;
            this.cmbModel.Items.AddRange(new object[] {
            "anthropic/claude-3.7-sonnet",
            "anthropic/claude-sonnet-4",
            "google/gemini-2.5-flash",
            "google/gemini-2.5-pro",
            "openai/gpt-4o",
            "openai/gpt-5"});
            this.cmbModel.Location = new System.Drawing.Point(0, 54);
            this.cmbModel.Name = "cmbModel";
            this.cmbModel.Size = new System.Drawing.Size(175, 21);
            this.cmbModel.Sorted = true;
            this.cmbModel.TabIndex = 2;
            // 
            // pnlBottom
            // 
            this.pnlBottom.AutoSize = true;
            this.pnlBottom.Controls.Add(this.pnlApiKeyBanner);
            this.pnlBottom.Controls.Add(this.pnlInput);
            this.pnlBottom.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlBottom.Location = new System.Drawing.Point(0, 639);
            this.pnlBottom.Margin = new System.Windows.Forms.Padding(0, 3, 3, 3);
            this.pnlBottom.Name = "pnlBottom";
            this.pnlBottom.Size = new System.Drawing.Size(885, 103);
            this.pnlBottom.TabIndex = 3;
            // 
            // pnlApiKeyBanner
            // 
            this.pnlApiKeyBanner.BackColor = System.Drawing.Color.Gold;
            this.pnlApiKeyBanner.Controls.Add(this.lnkOpenSettings);
            this.pnlApiKeyBanner.Controls.Add(this.lblNoApiKey);
            this.pnlApiKeyBanner.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.pnlApiKeyBanner.Location = new System.Drawing.Point(0, 0);
            this.pnlApiKeyBanner.Margin = new System.Windows.Forms.Padding(0);
            this.pnlApiKeyBanner.Name = "pnlApiKeyBanner";
            this.pnlApiKeyBanner.Padding = new System.Windows.Forms.Padding(6, 4, 6, 4);
            this.pnlApiKeyBanner.Size = new System.Drawing.Size(885, 28);
            this.pnlApiKeyBanner.TabIndex = 1;
            // 
            // lnkOpenSettings
            // 
            this.lnkOpenSettings.AutoSize = true;
            this.lnkOpenSettings.Location = new System.Drawing.Point(132, 8);
            this.lnkOpenSettings.Name = "lnkOpenSettings";
            this.lnkOpenSettings.Size = new System.Drawing.Size(74, 13);
            this.lnkOpenSettings.TabIndex = 1;
            this.lnkOpenSettings.TabStop = true;
            this.lnkOpenSettings.Text = "Open Settings";
            // 
            // lblNoApiKey
            // 
            this.lblNoApiKey.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
                        | System.Windows.Forms.AnchorStyles.Left)));
            this.lblNoApiKey.AutoSize = true;
            this.lblNoApiKey.Location = new System.Drawing.Point(12, 8);
            this.lblNoApiKey.Name = "lblNoApiKey";
            this.lblNoApiKey.Size = new System.Drawing.Size(114, 13);
            this.lblNoApiKey.TabIndex = 0;
            this.lblNoApiKey.Text = "No API key configured";
            // 
            // tabControl1
            // 
            this.tabControl1.Controls.Add(this.tabPage1);
            this.tabControl1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.tabControl1.Location = new System.Drawing.Point(0, 0);
            this.tabControl1.Name = "tabControl1";
            this.tabControl1.SelectedIndex = 0;
            this.tabControl1.Size = new System.Drawing.Size(885, 639);
            this.tabControl1.TabIndex = 4;
            // 
            // tabPage1
            // 
            this.tabPage1.Controls.Add(this.chatTranscript);
            this.tabPage1.Location = new System.Drawing.Point(4, 22);
            this.tabPage1.Name = "tabPage1";
            this.tabPage1.Size = new System.Drawing.Size(877, 613);
            this.tabPage1.TabIndex = 0;
            this.tabPage1.Text = "New Conversation";
            this.tabPage1.UseVisualStyleBackColor = true;
            // 
            // splitContainer1
            // 
            this.splitContainer1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.splitContainer1.IsSplitterFixed = true;
            this.splitContainer1.Location = new System.Drawing.Point(0, 24);
            this.splitContainer1.Margin = new System.Windows.Forms.Padding(0);
            this.splitContainer1.Name = "splitContainer1";
            this.splitContainer1.Panel1MinSize = 5;
            // 
            // splitContainer1.Panel2
            // 
            this.splitContainer1.Panel2.Controls.Add(this.tabControl1);
            this.splitContainer1.Panel2.Controls.Add(this.pnlBottom);
            this.splitContainer1.Size = new System.Drawing.Size(892, 742);
            this.splitContainer1.SplitterDistance = 6;
            this.splitContainer1.SplitterWidth = 1;
            this.splitContainer1.TabIndex = 1;
            // 
            // chatTranscript
            // 
            this.chatTranscript.AccessibleName = "Chat transcript";
            this.chatTranscript.BackColor = System.Drawing.SystemColors.ControlLightLight;
            this.chatTranscript.Dock = System.Windows.Forms.DockStyle.Fill;
            this.chatTranscript.ForeColor = System.Drawing.SystemColors.WindowText;
            this.chatTranscript.Location = new System.Drawing.Point(0, 0);
            this.chatTranscript.Margin = new System.Windows.Forms.Padding(0);
            this.chatTranscript.Name = "chatTranscript";
            this.chatTranscript.Size = new System.Drawing.Size(877, 613);
            this.chatTranscript.TabIndex = 0;
            // 
            // miHelp
            // 
            this.miHelp.DropDownItems.AddRange(new System.Windows.Forms.ToolStripItem[] {
            this.miApiKeysHelp});
            this.miHelp.Name = "miHelp";
            this.miHelp.Size = new System.Drawing.Size(40, 20);
            this.miHelp.Text = "Help";
            // 
            // miApiKeysHelp
            // 
            this.miApiKeysHelp.Name = "miApiKeysHelp";
            this.miApiKeysHelp.Size = new System.Drawing.Size(152, 22);
            this.miApiKeysHelp.Text = "API Keys";
            this.miApiKeysHelp.Click += new System.EventHandler(this.aPIKeysToolStripMenuItem_Click);
            // 
            // MainForm
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(892, 766);
            this.Controls.Add(this.splitContainer1);
            this.Controls.Add(this.msMain);
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MainMenuStrip = this.msMain;
            this.Name = "MainForm";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "GxPT - New Conversation";
            this.msMain.ResumeLayout(false);
            this.msMain.PerformLayout();
            this.pnlInput.ResumeLayout(false);
            this.pnlInput.PerformLayout();
            this.pnlInputRight.ResumeLayout(false);
            this.pnlBottom.ResumeLayout(false);
            this.pnlBottom.PerformLayout();
            this.pnlApiKeyBanner.ResumeLayout(false);
            this.pnlApiKeyBanner.PerformLayout();
            this.tabControl1.ResumeLayout(false);
            this.tabPage1.ResumeLayout(false);
            this.splitContainer1.Panel2.ResumeLayout(false);
            this.splitContainer1.Panel2.PerformLayout();
            this.splitContainer1.ResumeLayout(false);
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private ChatTranscriptControl chatTranscript;
        private System.Windows.Forms.MenuStrip msMain;
        private System.Windows.Forms.ToolStripMenuItem miFile;
        private System.Windows.Forms.ToolStripMenuItem miSettings;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator1;
        private System.Windows.Forms.ToolStripMenuItem miExit;
        private System.Windows.Forms.Panel pnlInput;
        private System.Windows.Forms.Button btnSend;
        private System.Windows.Forms.TextBox txtMessage;
        private System.Windows.Forms.Panel pnlInputRight;
        private System.Windows.Forms.ComboBox cmbModel;
        private System.Windows.Forms.Panel pnlBottom;
        private System.Windows.Forms.Panel pnlApiKeyBanner;
        private System.Windows.Forms.Label lblNoApiKey;
        private System.Windows.Forms.LinkLabel lnkOpenSettings;
        private System.Windows.Forms.ToolStripMenuItem miNewConversation;
        private System.Windows.Forms.TabControl tabControl1;
        private System.Windows.Forms.TabPage tabPage1;
        private System.Windows.Forms.ToolStripSeparator toolStripSeparator2;
        private System.Windows.Forms.ToolStripMenuItem closeConversationToolStripMenuItem;
        private System.Windows.Forms.SplitContainer splitContainer1;
        private System.Windows.Forms.ToolStripMenuItem miView;
        private System.Windows.Forms.ToolStripMenuItem miConversationHistory;
        private System.Windows.Forms.ToolStripMenuItem miHelp;
        private System.Windows.Forms.ToolStripMenuItem miApiKeysHelp;


    }
}

