using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace GxPT
{
    internal sealed class TabManager
    {
        private readonly MainForm _mainForm;
        private readonly TabControl _tabControl;
        private readonly Dictionary<TabPage, ChatTabContext> _tabContexts = new Dictionary<TabPage, ChatTabContext>();

        // Tab context menu
        private ContextMenuStrip _tabCtxMenu;
        private ToolStripMenuItem _miTabNew;
        private ToolStripMenuItem _miTabClose;
        private ToolStripMenuItem _miTabCloseOthers;
        private TabPage _tabCtxTarget;

        // Custom toolbar buttons
        private GlyphToolStripButton _btnNewTab;
        private GlyphToolStripButton _btnCloseTab;

        public event Action<TabPage> TabSelected;
        public event Action TabsChanged;

        // Per-tab chat context
        public sealed class ChatTabContext
        {
            public TabPage Page;
            public ChatTranscriptControl Transcript;
            public Conversation Conversation;
            public bool IsSending;
            public string SelectedModel;
            public bool NoSaveUntilUserSend;
            public List<AttachedFile> PendingAttachments = new List<AttachedFile>();
            // Pending edit of a prior user message (by transcript/history index)
            public bool PendingEditActive;
            public int PendingEditIndex = -1;
            // Model that was active when entering edit mode; used to detect resends due to model change
            public string PendingEditOriginalModel;
        }

        public TabManager(MainForm mainForm, TabControl tabControl, MenuStrip menuStrip)
        {
            _mainForm = mainForm;
            _tabControl = tabControl;

            InitializeTabControl();
            CreateTabContextMenu();
            AddCustomButtons(menuStrip);
        }

        public Dictionary<TabPage, ChatTabContext> TabContexts
        {
            get { return _tabContexts; }
        }

        private void InitializeTabControl()
        {
            if (_tabControl != null)
            {
                _tabControl.SelectedIndexChanged += (s, e) =>
                {
                    if (TabSelected != null) TabSelected(_tabControl.SelectedTab);
                };

                try
                {
                    _tabControl.DrawMode = TabDrawMode.Normal;
                    _tabControl.MouseDown -= tabControl1_MouseDown;
                    _tabControl.MouseDown += tabControl1_MouseDown;
                    _tabControl.MouseUp -= tabControl1_MouseUp;
                    _tabControl.MouseUp += tabControl1_MouseUp;
                }
                catch { }
            }
        }

        private void CreateTabContextMenu()
        {
            try
            {
                _tabCtxMenu = new ContextMenuStrip();
                _miTabNew = new ToolStripMenuItem("New Tab");
                _miTabClose = new ToolStripMenuItem("Close");
                _miTabCloseOthers = new ToolStripMenuItem("Close Others");

                _miTabNew.Click += delegate { CreateConversationTab(); };
                _miTabClose.Click += delegate { if (_tabCtxTarget != null) CloseConversationTab(_tabCtxTarget); };
                _miTabCloseOthers.Click += delegate { if (_tabCtxTarget != null) CloseOtherTabs(_tabCtxTarget); };

                _tabCtxMenu.Items.AddRange(new ToolStripItem[] { _miTabNew, new ToolStripSeparator(), _miTabClose, _miTabCloseOthers });
            }
            catch { }
        }

        private void AddCustomButtons(MenuStrip menuStrip)
        {
            try
            {
                if (menuStrip != null)
                {
                    // Ensure the menu strip displays item tooltips
                    try { menuStrip.ShowItemToolTips = true; }
                    catch { }

                    _btnNewTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Plus);
                    _btnNewTab.Margin = new Padding(2, 2, 2, 2);
                    _btnNewTab.ToolTipText = "New Tab";
                    _btnNewTab.Click += delegate { CreateConversationTab(); };
                    _btnNewTab.Alignment = ToolStripItemAlignment.Right;

                    _btnCloseTab = new GlyphToolStripButton(GlyphToolStripButton.GlyphType.Close);
                    _btnCloseTab.Margin = new Padding(2, 2, 3, 2);
                    _btnCloseTab.ToolTipText = "Close Tab";
                    _btnCloseTab.Click += delegate { CloseActiveConversationTab(); };
                    _btnCloseTab.Alignment = ToolStripItemAlignment.Right;

                    menuStrip.Items.Add(_btnCloseTab);
                    menuStrip.Items.Add(_btnNewTab);
                }
            }
            catch { }
        }

        public ChatTabContext SetupInitialConversationTab(TabPage initialTab, ChatTranscriptControl initialTranscript)
        {
            try
            {
                if (_tabControl == null || initialTab == null || initialTranscript == null)
                    return null;

                if (_tabContexts.ContainsKey(initialTab))
                    return _tabContexts[initialTab];

                var ctx = new ChatTabContext
                {
                    Page = initialTab,
                    Transcript = initialTranscript,
                    Conversation = new Conversation(_mainForm.GetClient()),
                    IsSending = false,
                    SelectedModel = _mainForm.GetConfiguredDefaultModel()
                };
                ctx.Conversation.SelectedModel = ctx.SelectedModel;
                _mainForm.EnsureConversationId(ctx.Conversation);
                // Wire edit request from transcript to input box
                try
                {
                    if (ctx.Transcript != null)
                    {
                        ctx.Transcript.UserMessageEditRequested += delegate(int index, string text)
                        {
                            try
                            {
                                // Map transcript index (user+assistant only) to conversation history index (skipping system)
                                int histIndex = MapTranscriptToHistoryIndex(ctx.Conversation, index);
                                if (histIndex < 0 || histIndex >= ctx.Conversation.History.Count)
                                    return;
                                var msg = ctx.Conversation.History[histIndex];
                                if (msg == null || !string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                                    return; // only allow editing user messages

                                var im = _mainForm.GetInputManager();
                                if (im != null) im.SetInputText(msg.Content ?? string.Empty, true);
                                ctx.PendingEditActive = true;
                                ctx.PendingEditIndex = histIndex;
                                // Capture the model at time of entering edit mode
                                ctx.PendingEditOriginalModel = ctx.SelectedModel;
                                // Seed pending attachments from the original message being edited
                                try
                                {
                                    var list = new List<AttachedFile>();
                                    if (msg.Attachments != null)
                                    {
                                        for (int i = 0; i < msg.Attachments.Count; i++)
                                        {
                                            var af = msg.Attachments[i];
                                            if (af == null) continue;
                                            list.Add(new AttachedFile(af.FileName, af.Content));
                                        }
                                    }
                                    ctx.PendingAttachments = list;
                                    // Refresh attachments banner UI
                                    try { _mainForm.RefreshAttachmentsBannerUi(); }
                                    catch { }
                                }
                                catch { }
                                _mainForm.SelectTab(ctx.Page);
                            }
                            catch { }
                        };
                    }
                }
                catch { }

                ctx.Conversation.NameGenerated += delegate(string name)
                {
                    try
                    {
                        if (_mainForm.IsHandleCreated)
                        {
                            _mainForm.BeginInvoke((MethodInvoker)delegate
                            {
                                ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                                _mainForm.UpdateWindowTitle();
                            });
                        }
                    }
                    catch { }
                };

                try { ctx.Page.Text = "New Conversation"; }
                catch { }

                // Apply transcript/message width from settings to the initial transcript
                try
                {
                    int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
                    if (tw <= 0) tw = 1000; if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                    int rawMp = (int)AppSettings.GetDouble("message_max_width", 90);
                    int mp = rawMp;
                    if (mp < 50 || mp > 100)
                    {
                        int computed = (rawMp <= 0) ? 90 : (int)Math.Round(100.0 * rawMp / Math.Max(1, tw));
                        if (computed < 50) computed = 50; if (computed > 100) computed = 100;
                        mp = computed;
                    }
                    if (mp < 50) mp = 50; if (mp > 100) mp = 100;
                    if (ctx.Transcript != null)
                    {
                        ctx.Transcript.MaxContentWidth = tw;
                        try { ctx.Transcript.BubbleWidthPercent = mp; }
                        catch { }
                    }
                }
                catch { }

                _tabContexts[initialTab] = ctx;
                if (TabsChanged != null) TabsChanged();
                return ctx;
            }
            catch { return null; }
        }

        public ChatTabContext CreateConversationTab()
        {
            if (_tabControl == null) return null;

            var page = new TabPage("New Conversation");
            page.UseVisualStyleBackColor = true;

            var transcript = new ChatTranscriptControl();
            transcript.Dock = DockStyle.Fill;
            _mainForm.ApplyFontSetting(transcript);
            page.Controls.Add(transcript);

            var ctx = new ChatTabContext
            {
                Page = page,
                Transcript = transcript,
                Conversation = new Conversation(_mainForm.GetClient()),
                IsSending = false,
                SelectedModel = _mainForm.GetConfiguredDefaultModel()
            };
            ctx.Conversation.SelectedModel = ctx.SelectedModel;
            _mainForm.EnsureConversationId(ctx.Conversation);

            // Wire edit request from transcript to input box
            try
            {
                if (ctx.Transcript != null)
                {
                    ctx.Transcript.UserMessageEditRequested += delegate(int index, string text)
                    {
                        try
                        {
                            // Map transcript index (user+assistant only) to conversation history index (skipping system)
                            int histIndex = MapTranscriptToHistoryIndex(ctx.Conversation, index);
                            if (histIndex < 0 || histIndex >= ctx.Conversation.History.Count)
                                return;
                            var msg = ctx.Conversation.History[histIndex];
                            if (msg == null || !string.Equals(msg.Role, "user", StringComparison.OrdinalIgnoreCase))
                                return; // only allow editing user messages

                            var im = _mainForm.GetInputManager();
                            if (im != null) im.SetInputText(msg.Content ?? string.Empty, true);
                            ctx.PendingEditActive = true;
                            ctx.PendingEditIndex = histIndex;
                            // Capture the model at time of entering edit mode
                            ctx.PendingEditOriginalModel = ctx.SelectedModel;
                            // Seed pending attachments from the original message being edited
                            try
                            {
                                var list = new List<AttachedFile>();
                                if (msg.Attachments != null)
                                {
                                    for (int i = 0; i < msg.Attachments.Count; i++)
                                    {
                                        var af = msg.Attachments[i];
                                        if (af == null) continue;
                                        list.Add(new AttachedFile(af.FileName, af.Content));
                                    }
                                }
                                ctx.PendingAttachments = list;
                                try { _mainForm.RefreshAttachmentsBannerUi(); }
                                catch { }
                            }
                            catch { }
                            _mainForm.SelectTab(ctx.Page);
                        }
                        catch { }
                    };
                }
            }
            catch { }

            ctx.Conversation.NameGenerated += delegate(string name)
            {
                try
                {
                    if (_mainForm.IsHandleCreated)
                    {
                        _mainForm.BeginInvoke((MethodInvoker)delegate
                        {
                            ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                            _mainForm.UpdateWindowTitle();
                        });
                    }
                }
                catch { }
            };

            _tabContexts[page] = ctx;

            _tabControl.TabPages.Add(page);
            try { _tabControl.SelectedTab = page; }
            catch { }

            // Apply transcript/message width from settings for newly created transcript
            try
            {
                int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
                if (tw <= 0) tw = 1000; if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                int rawMp = (int)AppSettings.GetDouble("message_max_width", 90);
                int mp = rawMp;
                if (mp < 50 || mp > 100)
                {
                    int computed = (rawMp <= 0) ? 90 : (int)Math.Round(100.0 * rawMp / Math.Max(1, tw));
                    if (computed < 50) computed = 50; if (computed > 100) computed = 100;
                    mp = computed;
                }
                if (mp < 50) mp = 50; if (mp > 100) mp = 100;
                try { transcript.MaxContentWidth = tw; }
                catch { }
                try { transcript.BubbleWidthPercent = mp; }
                catch { }

            }
            catch { }

            if (TabsChanged != null) TabsChanged();
            return ctx;
        }

        public ChatTabContext GetActiveContext()
        {
            try
            {
                if (_tabControl == null) return null;
                var page = _tabControl.SelectedTab;
                if (page == null) return null;
                ChatTabContext ctx;
                return _tabContexts.TryGetValue(page, out ctx) ? ctx : null;
            }
            catch { return null; }
        }

        public void CloseActiveConversationTab()
        {
            try
            {
                if (_tabControl == null) return;
                var page = _tabControl.SelectedTab;
                if (page == null) return;
                CloseConversationTab(page);
            }
            catch { }
        }

        public void CloseConversationTab(TabPage page)
        {
            if (page == null) return;

            ChatTabContext ctx;
            _tabContexts.TryGetValue(page, out ctx);

            if (_tabControl != null && _tabControl.TabPages.Count <= 1)
            {
                // Reset single remaining tab
                try
                {
                    if (ctx != null)
                    {
                        // Ensure the sidebar no longer thinks the previous conversation is still open on this page
                        try { _mainForm.UntrackOpenConversation(page); }
                        catch { }
                        if (ctx.Transcript != null) ctx.Transcript.ClearMessages();
                        ctx.Conversation = new Conversation(_mainForm.GetClient());
                        // Reset model to default on a fresh blank tab
                        try
                        {
                            ctx.SelectedModel = _mainForm.GetConfiguredDefaultModel();
                            ctx.Conversation.SelectedModel = ctx.SelectedModel;
                            _mainForm.SyncComboModelFromActiveTab();
                        }
                        catch { }
                        // re-hook name event
                        ctx.Conversation.NameGenerated += delegate(string name)
                        {
                            try
                            {
                                if (_mainForm.IsHandleCreated)
                                {
                                    _mainForm.BeginInvoke((MethodInvoker)delegate
                                    {
                                        ctx.Page.Text = string.IsNullOrEmpty(name) ? "Conversation" : name;
                                        _mainForm.UpdateWindowTitle();
                                    });
                                }
                            }
                            catch { }
                        };
                        page.Text = "New Conversation";
                        _mainForm.UpdateWindowTitle();
                    }
                }
                catch { }
                if (TabsChanged != null) TabsChanged();
                return;
            }

            try
            {
                int desiredIndex = -1;
                if (_tabControl != null)
                {
                    try
                    {
                        int idx = _tabControl.TabPages.IndexOf(page);
                        if (idx >= 0) desiredIndex = Math.Max(0, idx - 1);
                    }
                    catch { }
                }

                if (_tabContexts.ContainsKey(page))
                    _tabContexts.Remove(page);

                _mainForm.UntrackOpenConversation(page);

                if (_tabControl != null)
                {
                    _tabControl.TabPages.Remove(page);
                    try
                    {
                        if (_tabControl.TabPages.Count > 0)
                        {
                            if (desiredIndex < 0) desiredIndex = 0;
                            if (desiredIndex >= _tabControl.TabPages.Count)
                                desiredIndex = _tabControl.TabPages.Count - 1;
                            _tabControl.SelectedIndex = desiredIndex;
                        }
                    }
                    catch { }
                }

                try { page.Dispose(); }
                catch { }

                _mainForm.UpdateWindowTitle();
                if (TabsChanged != null) TabsChanged();
            }
            catch { }
        }

        private void CloseOtherTabs(TabPage keep)
        {
            try
            {
                if (_tabControl == null || keep == null) return;
                var toClose = new List<TabPage>();
                foreach (TabPage p in _tabControl.TabPages)
                {
                    if (!object.ReferenceEquals(p, keep)) toClose.Add(p);
                }
                foreach (var p in toClose)
                {
                    CloseConversationTab(p);
                }
            }
            catch { }
        }

        private void tabControl1_MouseDown(object sender, MouseEventArgs e)
        {
            try
            {
                if (_tabControl == null) return;

                if (e.Button == MouseButtons.Middle)
                {
                    for (int i = 0; i < _tabControl.TabPages.Count; i++)
                    {
                        var page = _tabControl.TabPages[i];
                        Rectangle r = _tabControl.GetTabRect(i);
                        if (r.Contains(e.Location))
                        {
                            CloseConversationTab(page);
                            return;
                        }
                    }
                    return;
                }
            }
            catch { }
        }

        private void tabControl1_MouseUp(object sender, MouseEventArgs e)
        {
            try
            {
                if (_tabControl == null || _tabCtxMenu == null) return;
                if (e.Button != MouseButtons.Right) return;

                _tabCtxTarget = null;
                for (int i = 0; i < _tabControl.TabPages.Count; i++)
                {
                    Rectangle r = _tabControl.GetTabRect(i);
                    if (r.Contains(e.Location))
                    {
                        _tabCtxTarget = _tabControl.TabPages[i];
                        try { _tabControl.SelectedTab = _tabCtxTarget; }
                        catch { }
                        break;
                    }
                }

                bool hasTarget = (_tabCtxTarget != null);
                _miTabClose.Enabled = hasTarget;
                _miTabCloseOthers.Enabled = hasTarget && _tabControl.TabPages.Count > 1;

                _tabCtxMenu.Show(_tabControl, e.Location);
            }
            catch { }
        }

        public void SelectTab(TabPage page)
        {
            try
            {
                if (_tabControl != null && _tabControl.TabPages.Contains(page))
                    _tabControl.SelectedTab = page;
            }
            catch { }
        }

        // Keyboard navigation helpers: cycle through tabs
        public void SelectNextTab()
        {
            try
            {
                if (_tabControl == null) return;
                int count = _tabControl.TabPages.Count;
                if (count <= 0) return;
                int idx = Math.Max(0, _tabControl.SelectedIndex);
                int next = (idx + 1) % count;
                _tabControl.SelectedIndex = next;
            }
            catch { }
        }

        public void SelectPreviousTab()
        {
            try
            {
                if (_tabControl == null) return;
                int count = _tabControl.TabPages.Count;
                if (count <= 0) return;
                int idx = Math.Max(0, _tabControl.SelectedIndex);
                int prev = (idx - 1 + count) % count;
                _tabControl.SelectedIndex = prev;
            }
            catch { }
        }

        public void ApplyFontSetting()
        {
            try
            {
                double fs = AppSettings.GetDouble("font_size", 0);
                if (fs <= 0) return;
                float size = (float)Math.Max(6, Math.Min(48, fs));

                if (_tabControl != null)
                {
                    _tabControl.Font = new Font(_tabControl.Font.FontFamily, size, _tabControl.Font.Style);
                    foreach (TabPage p in _tabControl.TabPages)
                    {
                        try { if (p != null) p.Font = new Font(p.Font.FontFamily, size, p.Font.Style); }
                        catch { }
                    }
                }

                foreach (var kv in _tabContexts)
                {
                    try
                    {
                        var t = kv.Value.Transcript;
                        if (t != null) t.Font = new Font(t.Font.FontFamily, size, t.Font.Style);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public void ApplyThemeToAllTranscripts()
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    try { if (kv.Value.Transcript != null) kv.Value.Transcript.RefreshTheme(); }
                    catch { }
                }
            }
            catch { }
        }

        // Compatibility helpers: apply transcript/message width settings across all transcripts.
        // These overloads allow older callers to compile regardless of signature.
        public void ApplyTranscriptWidthToAllTranscripts()
        {
            try
            {
                int tw = (int)AppSettings.GetDouble("transcript_max_width", 1000);
                if (tw <= 0) tw = 1000; if (tw < 300) tw = 300; if (tw > 1900) tw = 1900;
                int rawMp = (int)AppSettings.GetDouble("message_max_width", 90);
                int mp = rawMp;
                if (mp < 50 || mp > 100)
                {
                    int computed = (rawMp <= 0) ? 90 : (int)Math.Round(100.0 * rawMp / Math.Max(1, tw));
                    if (computed < 50) computed = 50; if (computed > 100) computed = 100;
                    mp = computed;
                }
                if (mp < 50) mp = 50; if (mp > 100) mp = 100;

                foreach (var kv in _tabContexts)
                {
                    var t = kv.Value != null ? kv.Value.Transcript : null;
                    if (t == null) continue;
                    try { t.MaxContentWidth = tw; }
                    catch { }
                    try { t.BubbleWidthPercent = mp; }
                    catch { }
                }
            }
            catch { }
        }

        public void ApplyTranscriptWidthToAllTranscripts(int maxContentWidth, int maxBubbleWidth)
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    var t = kv.Value != null ? kv.Value.Transcript : null;
                    if (t == null) continue;
                    try { t.MaxContentWidth = maxContentWidth; }
                    catch { }
                    try { t.MaxBubbleWidth = maxBubbleWidth; }
                    catch { }
                }
            }
            catch { }
        }

        // Back-compat: apply only content width; leave bubble width unchanged
        public void ApplyTranscriptWidthToAllTranscripts(int maxContentWidth)
        {
            try
            {
                foreach (var kv in _tabContexts)
                {
                    var t = kv.Value != null ? kv.Value.Transcript : null;
                    if (t == null) continue;
                    try { t.MaxContentWidth = maxContentWidth; }
                    catch { }
                }
            }
            catch { }
        }

        // Map a transcript message index (which excludes system messages) to the corresponding
        // history index in Conversation.History (which may include system messages).
        private static int MapTranscriptToHistoryIndex(Conversation convo, int transcriptIndex)
        {
            try
            {
                if (convo == null || convo.History == null) return -1;
                if (transcriptIndex < 0) return -1;
                int count = 0;
                for (int i = 0; i < convo.History.Count; i++)
                {
                    var m = convo.History[i];
                    if (m == null) continue;
                    if (string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                        continue; // not shown in transcript
                    if (count == transcriptIndex) return i;
                    count++;
                }
            }
            catch { }
            return -1;
        }

        // Custom ToolStripButton with copy-button-like hover/press visuals and +/x glyphs
        private sealed class GlyphToolStripButton : ToolStripButton
        {
            public enum GlyphType { Plus, Close }
            private bool _hover;
            private bool _pressed;
            private readonly GlyphType _glyph;

            public GlyphToolStripButton(GlyphType glyph)
            {
                _glyph = glyph;
                DisplayStyle = ToolStripItemDisplayStyle.None;
                AutoSize = false;
                Size = new Size(24, 20);
                Margin = new Padding(2);
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                _hover = true;
                Invalidate();
                base.OnMouseEnter(e);
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                _hover = false;
                _pressed = false;
                Invalidate();
                base.OnMouseLeave(e);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left)
                {
                    _pressed = true;
                    Invalidate();
                }
                base.OnMouseDown(e);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                _pressed = false;
                Invalidate();
                base.OnMouseUp(e);
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                Rectangle r = new Rectangle(0, 0, (int)this.Width - 1, (int)this.Height - 1);
                if (_hover || _pressed)
                {
                    int shade = _pressed ? 210 : 230;
                    using (var sb = new SolidBrush(Color.FromArgb(shade, shade, shade)))
                        g.FillRectangle(sb, r);
                    using (var pen = new Pen(Color.FromArgb(210, 210, 210)))
                        g.DrawRectangle(pen, r);
                }
                using (var pen = new Pen(Color.FromArgb(80, 80, 80), 2f))
                {
                    int cx = r.Left + r.Width / 2;
                    int cy = r.Top + r.Height / 2;
                    int len = Math.Min(r.Width, r.Height) / 2 - 3;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    if (_glyph == GlyphType.Plus)
                    {
                        g.DrawLine(pen, cx - len, cy, cx + len, cy);
                        g.DrawLine(pen, cx, cy - len, cx, cy + len);
                    }
                    else
                    {
                        g.DrawLine(pen, cx - len, cy - len, cx + len, cy + len);
                        g.DrawLine(pen, cx - len, cy + len, cx + len, cy - len);
                    }
                }
            }
        }
    }
}
