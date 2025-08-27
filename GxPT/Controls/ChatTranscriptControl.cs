// ChatTranscriptControl.cs
// WinForms owner-drawn chat transcript with basic Markdown rendering
// Target: .NET 3.5, Windows XP compatible

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using System.IO;

namespace GxPT
{
    public enum MessageRole { User, Assistant, System }

    [ToolboxItem(true)]
    public sealed partial class ChatTranscriptControl : UserControl
    {
        // ---------- Layout and rendering ----------
        private const int MarginOuter = 8;
        private const int GapBetweenBubbles = 6;
        private const int BubblePadding = 8;
        private const int BubbleRadius = 8;
        private const int ScrollStep = 40;
        // Configurable maximum bubble width (capped further by content area width)
        private int _maxBubbleWidth = 700; // midpoint between original 560 and 1000
        [Browsable(true)]
        [Category("Layout")]
        [Description("Maximum width, in pixels, for individual message bubbles. Actual width is min(MaxContentWidth, this value).")]
        [DefaultValue(700)]
        public int MaxBubbleWidth
        {
            get { return _maxBubbleWidth; }
            set
            {
                int v = (value < 1) ? 1 : value;
                if (v == _maxBubbleWidth) return;
                _maxBubbleWidth = v;
                Reflow();
                Invalidate();
            }
        }
        private const int BulletIndent = 18;
        private const int BulletGap = 8;
        private const int CodeBlockPadding = 6;
        private const int InlineCodePaddingX = 3;
        private const int InlineCodePaddingY = 1;

        // Bounded central content area width (designer-configurable)
        private int _maxContentWidth = 1000; // default maximum central area width

        [Browsable(true)]
        [Category("Layout")]
        [Description("Maximum width, in pixels, for the centered content area. If the control is narrower, it uses the control width.")]
        [DefaultValue(1000)]
        public int MaxContentWidth
        {
            get { return _maxContentWidth; }
            set
            {
                int v = (value < 1) ? 1 : value; // guard against invalid values
                if (v == _maxContentWidth) return;
                _maxContentWidth = v;
                Reflow();
                Invalidate();
            }
        }
        // Code block UI
        private const int CodeHScrollHeight = 12;      // height of horizontal scrollbar area
        private const int CodeHScrollThumbMin = 24;    // minimum thumb width
        private const int CodeCopyButtonHeight = 14;   // header area for copy button
        private const int CodeCopyButtonPad = 4;       // padding around copy text
        // Compute header height dynamically so the Copy button accommodates current font size
        private int GetCodeHeaderHeight()
        {
            int baseH = (_baseFont != null) ? _baseFont.Height : CodeCopyButtonHeight;
            // If we render language label in bold, account for its height too
            int boldH = (_boldFont != null) ? _boldFont.Height : baseH;
            int textH = Math.Max(baseH, boldH);
            // header height = text height + top/bottom padding, with a sane minimum
            return Math.Max(CodeCopyButtonHeight, textH + CodeCopyButtonPad * 2);
        }

        // Colors (theme-aware); default to light
        private Color _clrAppBack = SystemColors.Window;
        private Color _clrAppText = SystemColors.WindowText;

        private Color _clrUserBack = Color.FromArgb(225, 240, 255);
        private Color _clrUserBorder = Color.FromArgb(160, 190, 220);

        private Color _clrAsstBack = Color.FromArgb(235, 235, 235);
        private Color _clrAsstBorder = Color.FromArgb(200, 200, 200);

        private Color _clrSysBack = Color.FromArgb(255, 250, 220);
        private Color _clrSysBorder = Color.FromArgb(210, 200, 150);

        private Color _clrCodeBack = Color.FromArgb(245, 245, 245);
        private Color _clrCodeBorder = Color.FromArgb(210, 210, 210);
        private Color _clrInlineCodeBack = Color.FromArgb(240, 240, 240);
        private Color _clrInlineCodeBorder = Color.FromArgb(200, 200, 200);
        private Color _clrLink = Color.FromArgb(0, 102, 204);
        private Color _clrCopyHover = Color.FromArgb(230, 230, 230);
        private Color _clrCopyPressed = Color.FromArgb(210, 210, 210);
        private Color _clrScrollTrack = Color.FromArgb(235, 235, 235);
        private Color _clrScrollThumb = Color.FromArgb(200, 200, 200);
        private Color _clrScrollTrackBorder = Color.FromArgb(210, 210, 210);
        private Color _clrScrollThumbBorder = Color.FromArgb(160, 160, 160);
        private bool _isDarkTheme;


        // ---------- Fonts ----------
        private Font _baseFont;         // default UI font
        private Font _boldFont;
        private Font _italicFont;
        private Font _boldItalicFont;
        private Font _monoFont;         // code spans
        private Font _h1, _h2, _h3, _h4, _h5, _h6;
        // Cache for dynamically derived styled fonts based on a given base font (preserves size for headings)
        private readonly Dictionary<string, Font> _styledFontCache = new Dictionary<string, Font>();

        private readonly VScrollBar _vbar;
        private int _contentHeight;
        private int _scrollOffset;
        // Queue a deferred reflow to run after current layout (avoids stale viewport sizes)
        private bool _reflowQueued;

        // Stick-to-bottom behavior during streaming to avoid calling ScrollToBottom each delta
        private bool _stickToBottom;

        // Use modern ContextMenuStrip instead of legacy ContextMenu
        private readonly ContextMenuStrip _ctx;
        private MessageItem _ctxHit;

        // Raised when the user selects Edit… on a user message via context menu
        public event Action<int, string> UserMessageEditRequested;
        // Hover/drag state for code block UI
        private MessageItem _hoverCopyItem;
        private int _hoverCopyCodeIndex = -1;
        private MessageItem _copyPressedItem;
        private int _copyPressedCodeIndex = -1;
        private bool _draggingHScroll;
        private MessageItem _dragScrollItem;
        private int _dragScrollCodeIndex = -1;
        private Rectangle _dragScrollTrackRect; // track rect at drag start (virtual coords)
        private int _dragScrollContentWidth;    // content width at drag start
        private int _dragScrollViewportWidth;   // viewport width at drag start
        private int _dragStartMouseX;           // client X at drag start
        private int _dragStartScrollX;
        private MessageItem _hoverScrollItem;
        private int _hoverScrollCodeIndex = -1;
        private int _hoverScrollTableIndex = -1;
        private bool _dragScrollIsTable;        // dragging a table scrollbar vs code
        private bool _hoverScrollIsTable;       // hovering a table scrollbar vs code

        // Accumulator for high-precision (sub-120) vertical wheel deltas
        private double _wheelRemainderY;

        // ---------- Data ----------
        private sealed class MessageItem
        {
            public MessageRole Role;
            public string RawMarkdown;
            public Rectangle Bounds; // bubble bounds, virtual coords
            public int MeasuredHeight;
            public List<Block> Blocks; // parsed markdown
            public List<int> CodeScroll; // per-code-block horizontal scroll offsets
            public List<int> TableScroll; // per-table horizontal scroll offsets
            public List<AttachedFile> Attachments; // optional attachments to show as pills
            public List<Rectangle> AttachmentPillRects; // computed per-draw for hit testing
        }

        private readonly List<MessageItem> _items = new List<MessageItem>();
        private MessageItem _hoverAttachItem; private int _hoverAttachIndex = -1;
        private MessageItem _pressAttachItem; private int _pressAttachIndex = -1;

        // ---------- Batch update state ----------
        // Coalesce expensive reflow/paint while adding many messages (e.g., when opening history)
        private int _batchDepth;
        private bool _batchNeedsReflow;
        private bool _batchWantsScrollToBottom;
        private int _batchStartIndex = -1;        // index of first item potentially affected in this batch
        private bool _batchAppendOnly = false;    // true when only new items were appended at the end

        // ---------- ctor ----------
        public ChatTranscriptControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            ApplyThemeFromSettings();

            _vbar = new VScrollBar();
            _vbar.Dock = DockStyle.Right;
            _vbar.Visible = true;
            _vbar.ValueChanged += delegate { _scrollOffset = _vbar.Value; Invalidate(); };
            Controls.Add(_vbar);

            _ctx = new ContextMenuStrip();
            _ctx.Items.Add("Copy", null, delegate { if (_ctxHit != null) SafeClipboardSetText(_ctxHit.RawMarkdown ?? string.Empty); });

            _baseFont = this.Font;
            BuildFonts();

            this.AccessibleName = "Chat transcript";
            this.TabStop = true;

            // Listen for async highlight completions and invalidate the relevant region
            try
            {
                SyntaxHighlightingRenderer.SegmentsReady += OnSegmentsReady;
            }
            catch { }
        }

        // When async highlight for any block completes, repaint to pick up colored segments progressively.
        private void OnSegmentsReady(string key)
        {
            if (!IsHandleCreated) return;
            try
            {
                // Marshal to UI thread
                if (this.InvokeRequired)
                {
                    try { this.BeginInvoke((MethodInvoker)delegate { OnSegmentsReady(key); }); }
                    catch { }
                    return;
                }
                Invalidate();
            }
            catch { }
        }

        // Dispose is implemented in the Designer partial; avoid duplicate overrides here.

        // ---------- Batching API ----------
        // Use when adding or updating many messages to avoid per-item reflow/paint.
        public void BeginBatchUpdates()
        {
            _batchDepth++;
            if (_batchDepth == 1)
            {
                try { this.SuspendLayout(); }
                catch { }
                // Mark the starting point for potential append-only reflow
                _batchStartIndex = _items.Count;
                _batchAppendOnly = true;
            }
        }

        public void EndBatchUpdates()
        {
            EndBatchUpdates(false);
        }

        // When scrollToBottom is true, the view will jump to bottom once after the batch finishes.
        public void EndBatchUpdates(bool scrollToBottom)
        {
            if (_batchDepth <= 0) { _batchDepth = 0; return; }
            _batchDepth--;
            if (scrollToBottom) _batchWantsScrollToBottom = true;
            if (_batchDepth == 0)
            {
                try { this.ResumeLayout(false); }
                catch { }
                if (_batchNeedsReflow)
                {
                    // If we only appended new items since BeginBatchUpdates, we can reflow just the tail.
                    if (_batchAppendOnly && _batchStartIndex >= 0 && _batchStartIndex <= _items.Count)
                    {
                        ReflowAppendOnly(_batchStartIndex);
                    }
                    else
                    {
                        Reflow();
                    }
                    if (_batchWantsScrollToBottom) ScrollToBottom();
                    Invalidate();
                }
                _batchNeedsReflow = false;
                _batchWantsScrollToBottom = false;
                _batchStartIndex = -1;
                _batchAppendOnly = false;
            }
        }

        // Public helper to scroll by a wheel delta (positive=away from user, negative=toward)
        public void ScrollByWheelDelta(int wheelDelta)
        {
            try
            {
                if (!_vbar.Enabled) return;
                // Proportional pixel scroll: support precision deltas (e.g., trackpads) with remainder accumulation
                double pixelsD = -(wheelDelta / 120.0) * ScrollStep + _wheelRemainderY;
                int pixels = (int)System.Math.Truncate(pixelsD); // keep sign; leave fractional remainder for next tick
                _wheelRemainderY = pixelsD - pixels;
                if (pixels == 0) return; // nothing to move yet

                int view = System.Math.Max(0, ClientSize.Height);
                int max = System.Math.Max(0, _contentHeight - view);
                _scrollOffset = System.Math.Max(0, System.Math.Min(max, _scrollOffset + pixels));
                int allowedMax = System.Math.Max(0, _vbar.Maximum - _vbar.LargeChange + 1);
                _vbar.Value = System.Math.Max(_vbar.Minimum, System.Math.Min(allowedMax, _scrollOffset));
                Invalidate();
            }
            catch { }
        }

        // Called by the global router with screen coordinates and modifier keys for precise hover behavior.
        public void HandleHoverWheel(int wheelDelta, Point screenPoint, Keys modifiers)
        {
            try
            {
                Point clientPt = PointToClient(screenPoint);
                if ((modifiers & Keys.Shift) == Keys.Shift)
                {
                    var ui = HitTestCodeUI(clientPt);
                    if (ui.Hit && ui.ContentWidth > ui.ViewportWidth && ui.Item != null)
                    {
                        int hStep = Math.Max(16, ScrollStep);
                        // Proportional horizontal scroll for precision input; round to nearest pixel
                        int deltaX = (int)System.Math.Round(-(wheelDelta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        if (ui.IsTable)
                        {
                            int idx = ui.TableIndex;
                            if (ui.Item.TableScroll == null) ui.Item.TableScroll = new List<int>();
                            while (ui.Item.TableScroll.Count <= idx) ui.Item.TableScroll.Add(0);
                            int current = ui.Item.TableScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.TableScroll[idx] = next;
                            Invalidate();
                            return;
                        }
                        else if (ui.CodeIndex >= 0)
                        {
                            int idx = ui.CodeIndex;
                            if (ui.Item.CodeScroll == null) ui.Item.CodeScroll = new List<int>();
                            while (ui.Item.CodeScroll.Count <= idx) ui.Item.CodeScroll.Add(0);
                            int current = ui.Item.CodeScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.CodeScroll[idx] = next;
                            Invalidate();
                            return;
                        }
                    }
                }
                // Fallback to normal vertical scroll
                ScrollByWheelDelta(wheelDelta);
            }
            catch { }
        }


        public void RefreshTheme()
        {
            ApplyThemeFromSettings();
            Reflow();
            Invalidate();
        }

        private void ApplyThemeFromSettings()
        {
            // Read theme from AppSettings; fallback to light
            string theme = null;
            try { theme = AppSettings.GetString("theme"); }
            catch { theme = null; }
            bool dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
            _isDarkTheme = dark;

            if (dark)
            {
                // App background and text
                _clrAppBack = Color.FromArgb(0x24, 0x27, 0x3A); // Macchiato Base
                _clrAppText = Color.FromArgb(230, 230, 230); // light text
                // Bubbles
                _clrUserBack = Color.FromArgb(0x66, 0x00, 0x20); // #660020 user bubble background
                _clrUserBorder = Color.FromArgb(0x99, 0x00, 0x30); // #990030 user bubble border (darker)
                _clrAsstBack = Color.FromArgb(48, 49, 52);   // darker grey
                _clrAsstBorder = Color.FromArgb(70, 72, 75);
                _clrSysBack = Color.FromArgb(64, 60, 40);    // muted warm
                _clrSysBorder = Color.FromArgb(90, 85, 60);
                // Code blocks
                _clrCodeBack = Color.FromArgb(28, 29, 31);
                _clrCodeBorder = Color.FromArgb(70, 72, 75);
                _clrInlineCodeBack = Color.FromArgb(45, 46, 49);
                _clrInlineCodeBorder = Color.FromArgb(70, 72, 75);
                _clrLink = Color.FromArgb(120, 170, 255);
                _clrCopyHover = Color.FromArgb(60, 62, 66);
                _clrCopyPressed = Color.FromArgb(52, 54, 58);
                _clrScrollTrack = Color.FromArgb(45, 46, 49);
                _clrScrollThumb = Color.FromArgb(90, 92, 96);
                _clrScrollTrackBorder = Color.FromArgb(70, 72, 75);
                _clrScrollThumbBorder = Color.FromArgb(110, 112, 116);
            }
            else
            {
                _clrAppBack = SystemColors.Window;
                _clrAppText = SystemColors.WindowText;
                _clrUserBack = Color.FromArgb(225, 240, 255);
                _clrUserBorder = Color.FromArgb(160, 190, 220);
                _clrAsstBack = Color.FromArgb(235, 235, 235);
                _clrAsstBorder = Color.FromArgb(200, 200, 200);
                _clrSysBack = Color.FromArgb(255, 250, 220);
                _clrSysBorder = Color.FromArgb(210, 200, 150);
                _clrCodeBack = Color.FromArgb(245, 245, 245);
                _clrCodeBorder = Color.FromArgb(210, 210, 210);
                _clrInlineCodeBack = Color.FromArgb(240, 240, 240);
                _clrInlineCodeBorder = Color.FromArgb(200, 200, 200);
                _clrLink = Color.FromArgb(0, 102, 204);
                _clrCopyHover = Color.FromArgb(230, 230, 230);
                _clrCopyPressed = Color.FromArgb(210, 210, 210);
                _clrScrollTrack = Color.FromArgb(235, 235, 235);
                _clrScrollThumb = Color.FromArgb(200, 200, 200);
                _clrScrollTrackBorder = Color.FromArgb(210, 210, 210);
                _clrScrollThumbBorder = Color.FromArgb(160, 160, 160);
            }

            BackColor = _clrAppBack;
            ForeColor = _clrAppText;
            Invalidate();
        }

        private void BuildFonts()
        {
            DisposeFonts();
            _baseFont = this.Font ?? new Font("Tahoma", 9f);
            _boldFont = new Font(_baseFont, FontStyle.Bold);
            _italicFont = new Font(_baseFont, FontStyle.Italic);
            _boldItalicFont = new Font(_baseFont, FontStyle.Bold | FontStyle.Italic);

            try { _monoFont = new Font("Consolas", _baseFont.Size); }
            catch { _monoFont = new Font("Courier New", _baseFont.Size); }

            _h1 = new Font(_baseFont.FontFamily, _baseFont.Size + 8, FontStyle.Bold);
            _h2 = new Font(_baseFont.FontFamily, _baseFont.Size + 6, FontStyle.Bold);
            _h3 = new Font(_baseFont.FontFamily, _baseFont.Size + 4, FontStyle.Bold);
            _h4 = new Font(_baseFont.FontFamily, _baseFont.Size + 2, FontStyle.Bold);
            _h5 = new Font(_baseFont.FontFamily, _baseFont.Size + 1, FontStyle.Bold);
            _h6 = new Font(_baseFont.FontFamily, _baseFont.Size, FontStyle.Bold);
        }

        private void DisposeFonts()
        {
            if (_boldFont != null) _boldFont.Dispose();
            if (_italicFont != null) _italicFont.Dispose();
            if (_boldItalicFont != null) _boldItalicFont.Dispose();
            if (_monoFont != null) _monoFont.Dispose();
            if (_h1 != null) _h1.Dispose();
            if (_h2 != null) _h2.Dispose();
            if (_h3 != null) _h3.Dispose();
            if (_h4 != null) _h4.Dispose();
            if (_h5 != null) _h5.Dispose();
            if (_h6 != null) _h6.Dispose();
            _boldFont = _italicFont = _boldItalicFont = _monoFont = null;
            _h1 = _h2 = _h3 = _h4 = _h5 = _h6 = null;
            // Dispose any cached styled fonts
            if (_styledFontCache != null && _styledFontCache.Count > 0)
            {
                try
                {
                    foreach (var kv in _styledFontCache) { if (kv.Value != null) kv.Value.Dispose(); }
                }
                catch { }
                _styledFontCache.Clear();
            }
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            BuildFonts();
            Reflow();
            Invalidate();
        }

        // ---------- Public API ----------
        public void AddMessage(MessageRole role, string markdown)
        {
            if (markdown == null) markdown = string.Empty;
            var blocks = MarkdownParser.ParseMarkdown(markdown);
            AddParsedMessage(role, markdown, blocks, null);
        }

        public void AddMessage(MessageRole role, string markdown, List<AttachedFile> attachments)
        {
            if (markdown == null) markdown = string.Empty;
            var blocks = MarkdownParser.ParseMarkdown(markdown);
            AddParsedMessage(role, markdown, blocks, attachments);
        }

        // Add and return the index of the inserted message (to support targeted updates later)
        public int AddMessageGetIndex(MessageRole role, string markdown)
        {
            AddMessage(role, markdown);
            return _items.Count - 1;
        }

        public void ClearMessages()
        {
            _items.Clear();
            _contentHeight = 0;
            _scrollOffset = 0;
            UpdateScrollbar();
            Invalidate();
        }

        // Replace the content of the last message if it exists
        public void UpdateLastMessage(string markdown)
        {
            if (_items.Count == 0) return;
            var it = _items[_items.Count - 1];
            it.RawMarkdown = markdown ?? string.Empty;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // reset code scrolls to match blocks
            it.CodeScroll = new List<int>();
            int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
            for (int i = 0; i < codes; i++) it.CodeScroll.Add(0);
            // reset table scrolls to match blocks
            it.TableScroll = new List<int>();
            int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
            for (int i = 0; i < tables; i++) it.TableScroll.Add(0);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item; cannot append-only reflow safely
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Replace content of a specific message by index; safe no-op if out of range
        public void UpdateMessageAt(int index, string markdown)
        {
            if (index < 0 || index >= _items.Count) return;
            var it = _items[index];
            it.RawMarkdown = markdown ?? string.Empty;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // reset code scrolls to match blocks
            it.CodeScroll = new List<int>();
            int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
            for (int i = 0; i < codes; i++) it.CodeScroll.Add(0);
            // reset table scrolls to match blocks
            it.TableScroll = new List<int>();
            int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
            for (int i = 0; i < tables; i++) it.TableScroll.Add(0);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Append text to the last message (useful for streaming); will keep as a single paragraph
        public void AppendToLastMessage(string delta)
        {
            if (delta == null) return;
            if (_items.Count == 0)
            {
                AddMessage(MessageRole.Assistant, delta);
                return;
            }
            var it = _items[_items.Count - 1];
            it.RawMarkdown = (it.RawMarkdown ?? string.Empty) + delta;
            it.Blocks = MarkdownParser.ParseMarkdown(it.RawMarkdown);
            // Defer heavy layout to coalesce with other updates
            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchAppendOnly = false; // modified existing item
            }
            else
            {
                Invalidate();
                ReflowSoon();
            }
        }

        // Add a message with pre-parsed markdown blocks (useful to parse off the UI thread)
        public void AddParsedMessage(MessageRole role, string markdown, List<Block> blocks, List<AttachedFile> attachments)
        {
            if (markdown == null) markdown = string.Empty;
            if (blocks == null) blocks = MarkdownParser.ParseMarkdown(markdown);
            var item = new MessageItem
            {
                Role = role,
                RawMarkdown = markdown,
                Blocks = blocks,
                CodeScroll = new List<int>(),
                TableScroll = new List<int>(),
                Attachments = (attachments != null && attachments.Count > 0) ? new List<AttachedFile>(attachments) : null,
                AttachmentPillRects = null
            };
            int codeCount = 0; foreach (var b in item.Blocks) if (b.Type == BlockType.CodeBlock) codeCount++;
            for (int i = 0; i < codeCount; i++) item.CodeScroll.Add(0);
            int tableCount = 0; foreach (var b in item.Blocks) if (b.Type == BlockType.Table) tableCount++;
            for (int i = 0; i < tableCount; i++) item.TableScroll.Add(0);
            _items.Add(item);

            if (_batchDepth > 0)
            {
                _batchNeedsReflow = true;
                _batchWantsScrollToBottom = true;
                // still append-only if we keep appending to end
            }
            else
            {
                Reflow();
                ScrollToBottom();
                Invalidate();
                ReflowSoon();
            }
        }

        // ---------- Layout ----------
        private void Reflow()
        {
            using (Graphics g = CreateGraphics())
            {
                int y = MarginOuter;
                int innerWidth = Math.Max(0, ClientSize.Width - _vbar.Width - 2 * MarginOuter);
                // Determine the bounded content area (centered) within which bubbles align
                int areaWidth = Math.Min(innerWidth, _maxContentWidth);
                int areaLeft = MarginOuter + Math.Max(0, (innerWidth - areaWidth) / 2);
                int usableWidth = Math.Min(areaWidth, _maxBubbleWidth);

                foreach (var it in _items)
                {
                    if (it.CodeScroll == null) it.CodeScroll = new List<int>();
                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                    // ensure length matches number of code blocks
                    int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
                    while (it.CodeScroll.Count < codes) it.CodeScroll.Add(0);
                    if (it.CodeScroll.Count > codes) it.CodeScroll.RemoveRange(codes, it.CodeScroll.Count - codes);
                    // ensure length matches number of table blocks
                    int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
                    while (it.TableScroll.Count < tables) it.TableScroll.Add(0);
                    if (it.TableScroll.Count > tables) it.TableScroll.RemoveRange(tables, it.TableScroll.Count - tables);
                    Size bubbleSize = MeasureBubble(it, usableWidth);
                    int xLeft;
                    if (it.Role == MessageRole.User)
                    {
                        // User messages: right-aligned, but ensure minimum width
                        int minUserWidth = Math.Min(usableWidth, Math.Max(200, usableWidth / 2));
                        bubbleSize.Width = Math.Max(bubbleSize.Width, minUserWidth);
                        xLeft = areaLeft + areaWidth - bubbleSize.Width;
                    }
                    else
                    {
                        // Assistant/System messages: left-aligned
                        xLeft = areaLeft;
                    }

                    it.MeasuredHeight = bubbleSize.Height;
                    it.Bounds = new Rectangle(xLeft, y, bubbleSize.Width, bubbleSize.Height);
                    y += bubbleSize.Height + GapBetweenBubbles;
                }

                _contentHeight = y + MarginOuter;
            }

            UpdateScrollbar();
        }

        // Reflow only newly appended items from startIndex to end, positioning them after existing content.
        // Assumes earlier items' bounds are already valid and control width hasn't changed significantly mid-batch.
        private void ReflowAppendOnly(int startIndex)
        {
            if (startIndex < 0) { Reflow(); return; }
            using (Graphics g = CreateGraphics())
            {
                int innerWidth = Math.Max(0, ClientSize.Width - _vbar.Width - 2 * MarginOuter);
                int areaWidth = Math.Min(innerWidth, _maxContentWidth);
                int areaLeft = MarginOuter + Math.Max(0, (innerWidth - areaWidth) / 2);
                int usableWidth = Math.Min(areaWidth, _maxBubbleWidth);

                int y;
                if (startIndex == 0)
                {
                    y = MarginOuter;
                }
                else
                {
                    // Continue below the last previously laid-out item
                    var prev = _items[startIndex - 1];
                    y = prev.Bounds.Bottom + GapBetweenBubbles;
                }

                for (int idx = startIndex; idx < _items.Count; idx++)
                {
                    var it = _items[idx];
                    if (it.CodeScroll == null) it.CodeScroll = new List<int>();
                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                    // ensure length matches number of code/table blocks
                    int codes = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.CodeBlock) codes++;
                    while (it.CodeScroll.Count < codes) it.CodeScroll.Add(0);
                    if (it.CodeScroll.Count > codes) it.CodeScroll.RemoveRange(codes, it.CodeScroll.Count - codes);
                    int tables = 0; foreach (var b in it.Blocks) if (b.Type == BlockType.Table) tables++;
                    while (it.TableScroll.Count < tables) it.TableScroll.Add(0);
                    if (it.TableScroll.Count > tables) it.TableScroll.RemoveRange(tables, it.TableScroll.Count - tables);

                    Size bubbleSize = MeasureBubble(it, usableWidth);
                    int xLeft;
                    if (it.Role == MessageRole.User)
                    {
                        int minUserWidth = Math.Min(usableWidth, Math.Max(200, usableWidth / 2));
                        bubbleSize.Width = Math.Max(bubbleSize.Width, minUserWidth);
                        xLeft = areaLeft + areaWidth - bubbleSize.Width;
                    }
                    else
                    {
                        xLeft = areaLeft;
                    }
                    it.MeasuredHeight = bubbleSize.Height;
                    it.Bounds = new Rectangle(xLeft, y, bubbleSize.Width, bubbleSize.Height);
                    y += bubbleSize.Height + GapBetweenBubbles;
                }

                _contentHeight = y + MarginOuter;
            }

            UpdateScrollbar();
        }

        private Size MeasureBubble(MessageItem it, int maxBubbleWidth)
        {
            // Bubble width determined by content width + padding
            int textMax = Math.Max(40, maxBubbleWidth - 2 * BubblePadding);

            int h = BubblePadding; // top padding
            int wUsed = 0;

            // Maintain numbering counters across blocks so ordered lists continue through sublists
            var numberedCounters = new Dictionary<int, int>(); // key: indent level, value: last number emitted

            for (int i = 0; i < it.Blocks.Count; i++)
            {
                var blk = it.Blocks[i];
                // Reset numbering when leaving list context (paragraphs, headings, code)
                if (blk.Type != BlockType.NumberedList && blk.Type != BlockType.BulletList)
                    numberedCounters.Clear();

                Size sz = MeasureBlock(blk, textMax, numberedCounters);
                h += sz.Height;

                // Add spacing that matches the drawing code
                if (blk.Type == BlockType.Heading)
                    h += 4;
                else if (blk.Type == BlockType.Paragraph)
                    h += 2;
                else if (blk.Type == BlockType.CodeBlock)
                    h += 4;
                // Lists don't add extra spacing after themselves

                wUsed = Math.Max(wUsed, sz.Width);
            }

            h += BubblePadding; // bottom padding

            // Space for attachments pills (below content, within bubble padding)
            int attachH = MeasureAttachmentsHeight(it, textMax);
            if (attachH > 0)
            {
                h += attachH + 4; // small gap above pills
            }

            // Ensure minimum width and don't exceed maximum
            wUsed = Math.Max(wUsed, 100); // minimum content width
            int bubbleW = Math.Min(maxBubbleWidth, wUsed + 2 * BubblePadding);

            return new Size(bubbleW, Math.Max(24, h));
        }

        private int MeasureAttachmentsHeight(MessageItem it, int contentWidth)
        {
            if (it.Attachments == null || it.Attachments.Count == 0) return 0;
            int x = 0;
            int y = 0;
            int lineH = Math.Max(_baseFont.Height + 6, 18);
            for (int i = 0; i < it.Attachments.Count; i++)
            {
                string name = it.Attachments[i] != null ? (it.Attachments[i].FileName ?? "(file)") : "(file)";
                Size sz = TextRenderer.MeasureText(name, _baseFont, new Size(int.MaxValue / 4, int.MaxValue / 4), TextFormatFlags.NoPadding);
                int pillW = Math.Min(contentWidth, sz.Width + 16);
                int pillH = lineH;
                if (x > 0 && x + pillW > contentWidth)
                {
                    y += pillH + 4;
                    x = 0;
                }
                x += pillW + 6;
            }
            y += lineH;
            return y;
        }

        private Size MeasureBlock(Block blk, int maxWidth, Dictionary<int, int> numberedCounters)
        {
            switch (blk.Type)
            {
                case BlockType.Heading:
                    {
                        var h = (HeadingBlock)blk;
                        Font f = GetHeadingFont(h.Level);
                        return MeasureInlineParagraph(h.Inlines, f, maxWidth, true);
                    }
                case BlockType.Paragraph:
                    {
                        var p = (ParagraphBlock)blk;
                        return MeasureInlineParagraph(p.Inlines, _baseFont, maxWidth, true);
                    }
                case BlockType.BulletList:
                    {
                        var list = (BulletListBlock)blk;
                        int y = 0;
                        int w = 0;
                        foreach (var item in list.Items)
                        {
                            // measure bullet glyph + indented paragraph
                            int bulletWidth = BulletIndent + (item.IndentLevel * BulletIndent);
                            Size sz = MeasureInlineParagraph(item.Content, _baseFont, maxWidth - bulletWidth, true);
                            y += Math.Max(sz.Height, _baseFont.Height);
                            y += 2;
                            w = Math.Max(w, bulletWidth + sz.Width);
                        }
                        return new Size(Math.Min(maxWidth, w), y);
                    }
                case BlockType.NumberedList:
                    {
                        var list = (NumberedListBlock)blk;
                        int y = 0;
                        int w = 0;
                        foreach (var item in list.Items)
                        {
                            // Determine current number for this indent level (continue across blocks)
                            int indent = item.IndentLevel;
                            // Remove deeper indent counters when indent decreases
                            if (numberedCounters != null && numberedCounters.Count > 0)
                            {
                                var toRemove = new List<int>();
                                foreach (var k in numberedCounters.Keys)
                                    if (k > indent) toRemove.Add(k);
                                for (int r = 0; r < toRemove.Count; r++) numberedCounters.Remove(toRemove[r]);
                            }
                            int current;
                            if (numberedCounters == null || !numberedCounters.TryGetValue(indent, out current)) current = 0;
                            int itemNumber = current + 1;
                            if (numberedCounters != null) numberedCounters[indent] = itemNumber;
                            // measure number + indented paragraph
                            string numberText = itemNumber.ToString() + ".";
                            Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                            int numberWidth = numberSize.Width + 4 + (item.IndentLevel * BulletIndent); // 4px gap after number
                            Size sz = MeasureInlineParagraph(item.Content, _baseFont, maxWidth - numberWidth, true);
                            y += Math.Max(sz.Height, _baseFont.Height);
                            y += 2;
                            w = Math.Max(w, numberWidth + sz.Width);
                        }
                        return new Size(Math.Min(maxWidth, w), y);
                    }
                case BlockType.CodeBlock:
                    {
                        var c = (CodeBlock)blk;
                        // Measure colored segments without wrapping to know full content width
                        using (Graphics g = CreateGraphics())
                        {
                            // Enqueue for async highlight so it gets processed soon; enqueue in top-to-bottom order to get bottom-up processing
                            SyntaxHighlightingRenderer.EnqueueHighlight(c.Language, _isDarkTheme, c.Text, _monoFont);
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(c.Text, c.Language, _monoFont, _isDarkTheme);
                            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int viewportW = Math.Max(0, maxWidth - 2 * CodeBlockPadding);
                            bool needH = content.Width > viewportW;
                            int boxW = Math.Min(maxWidth, Math.Max(0, Math.Min(content.Width + 2 * CodeBlockPadding, maxWidth)));
                            int textH = Math.Max(_monoFont.Height, content.Height);
                            int headerH = GetCodeHeaderHeight();
                            int boxH = textH + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                            return new Size(Math.Max(24, boxW), Math.Max(headerH + 2 * CodeBlockPadding, boxH));
                        }
                    }
                case BlockType.Table:
                    {
                        var t = (TableBlock)blk;
                        // Measure each cell as inline paragraphs to compute column widths and total height
                        int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                        if (cols == 0) return new Size(0, 0);
                        int cellPad = 6;
                        int border = 1;
                        int[] colWidths = new int[cols];
                        int rowHeightHeader = 0;
                        int contentWidth = border; // total intrinsic content width of the table
                        // Header
                        for (int c = 0; c < cols; c++)
                        {
                            var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                            Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                            colWidths[c] = Math.Max(colWidths[c], sz.Width);
                            rowHeightHeader = Math.Max(rowHeightHeader, sz.Height);
                        }
                        // Rows
                        int[] rowHeights = new int[Math.Max(0, t.Rows.Count)];
                        for (int r = 0; r < t.Rows.Count; r++)
                        {
                            int rowH = 0;
                            var row = t.Rows[r];
                            for (int c = 0; c < cols; c++)
                            {
                                var inl = (c < row.Count) ? row[c] : new List<InlineRun>();
                                Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                rowH = Math.Max(rowH, sz.Height);
                            }
                            rowHeights[r] = rowH;
                        }
                        int totalW = border; for (int c = 0; c < cols; c++) totalW += colWidths[c] + cellPad * 2 + border; contentWidth = totalW;
                        int totalH = border + rowHeightHeader + cellPad * 2 + border; for (int r = 0; r < rowHeights.Length; r++) totalH += rowHeights[r] + cellPad * 2 + border;
                        bool needH = totalW > maxWidth;
                        // Clamp returned width to available max; add h-scroll height when needed
                        return new Size(Math.Min(maxWidth, totalW), totalH + (needH ? CodeHScrollHeight : 0));
                    }
            }
            return Size.Empty;
        }

        private Size MeasureInlineParagraph(List<InlineRun> runs, Font baseFont, int maxWidth, bool addBottomGap)
        {
            int x = 0;
            int y = 0;
            int lineHeight = baseFont.Height;
            int maxLineWidth = 0;

            foreach (var seg in WordWrapRuns(runs, baseFont, maxWidth))
            {
                if (seg.IsNewLine)
                {
                    y += lineHeight + 2; // Match the drawing spacing
                    maxLineWidth = Math.Max(maxLineWidth, x);
                    x = 0;
                    lineHeight = baseFont.Height;
                    continue;
                }

                // track tallest on the current line
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
                x += seg.Rect.Width;
            }

            // add last line
            y += lineHeight + 2; // Match the drawing spacing

            maxLineWidth = Math.Max(maxLineWidth, x);

            return new Size(Math.Min(maxWidth, maxLineWidth), y);
        }

        private struct LayoutSeg
        {
            public bool IsNewLine;
            public int LineWidth;
            public Font Font;
            public string Text;
            public Rectangle Rect;
            public bool IsInlineCode;
            public bool IsLink;
            public string LinkUrl;
        }

        private Font GetRunFont(RunStyle st, Font baseFont)
        {
            // Derive styled fonts from the provided baseFont so heading sizes are preserved.
            bool b = (st & RunStyle.Bold) != 0;
            bool i = (st & RunStyle.Italic) != 0;
            if (!b && !i) return baseFont;

            // Start from the baseFont's existing style (e.g., headings are already Bold)
            FontStyle fs = baseFont.Style;
            if (b) fs |= FontStyle.Bold;
            if (i) fs |= FontStyle.Italic;

            // Cache by family|size|style to avoid creating too many Font instances
            string key = baseFont.FontFamily.Name + "|" + baseFont.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + ((int)fs).ToString();
            Font cached;
            if (_styledFontCache.TryGetValue(key, out cached) && cached != null)
                return cached;

            try
            {
                var derived = new Font(baseFont, fs);
                _styledFontCache[key] = derived;
                return derived;
            }
            catch
            {
                // Fallback to base or prebuilt fonts if derivation fails
                if (b && i) return _boldItalicFont ?? baseFont;
                if (b) return _boldFont ?? baseFont;
                if (i) return _italicFont ?? baseFont;
                return baseFont;
            }
        }

        private IEnumerable<LayoutSeg> WordWrapRuns(List<InlineRun> runs, Font baseFont, int maxWidth)
        {
            // Greedy word wrapping across style runs.
            int x = 0;
            int lineWidth = 0;
            int lineHeight = baseFont.Height;

            using (Graphics g = CreateGraphics())
            {
                // Use typographic metrics to avoid GDI+ extra side bearings padding
                using (var fmt = StringFormat.GenericTypographic)
                {
                    fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                    foreach (var r in runs)
                    {
                        bool isCode = (r.Style & RunStyle.Code) != 0;
                        bool isLink = (r.Style & RunStyle.Link) != 0;
                        Font f = isCode ? _monoFont : GetRunFont(r.Style, baseFont);
                        // Split by spaces, keep separators
                        var parts = SplitWordsPreserveSpaces(r.Text);
                        foreach (string part in parts)
                        {
                            string text = part;
                            if (text == "\n")
                            {
                                // Hard line break
                                yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth };
                                x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                                continue;
                            }

                            SizeF szF = g.MeasureString(text.Length == 0 ? " " : text, f, PointF.Empty, fmt);
                            int partWidth = (int)Math.Ceiling(szF.Width);
                            int partHeight = (int)Math.Ceiling(szF.Height);

                            bool needsBreak = (x > 0 && x + partWidth > maxWidth);
                            if (needsBreak)
                            {
                                // break line
                                yield return new LayoutSeg { IsNewLine = true, LineWidth = lineWidth };
                                x = 0; lineWidth = 0; lineHeight = baseFont.Height;
                            }

                            // emit segment
                            yield return new LayoutSeg
                            {
                                IsNewLine = false,
                                Font = f,
                                Text = text,
                                Rect = new Rectangle(x, 0, partWidth, partHeight),
                                IsInlineCode = isCode,
                                IsLink = isLink,
                                LinkUrl = isLink ? r.LinkUrl : null
                            };

                            x += partWidth;
                            lineWidth += partWidth;
                            lineHeight = Math.Max(lineHeight, partHeight);
                        }
                    }
                }
            }
        }

        private static List<string> SplitWordsPreserveSpaces(string s)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(s)) { parts.Add(""); return parts; }

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n')
                {
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    parts.Add("\n");
                }
                else if (c == ' ')
                {
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    parts.Add(" ");
                }
                else
                {
                    sb.Append(c);
                }
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            if (parts.Count == 0) parts.Add("");
            return parts;
        }

        // ---------- Painting ----------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Reflow();
            Invalidate();
            // Ensure a second pass after parent has finished resizing
            ReflowSoon();
        }

        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            base.SetBoundsCore(x, y, width, height, specified);
            // Ensure scrollbar is updated when bounds change
            if (IsHandleCreated && (specified & (BoundsSpecified.Width | BoundsSpecified.Height)) != 0)
            {
                UpdateScrollbar();
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Ensure scrollbar is properly initialized when handle is created
            ReflowSoon();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible)
            {
                // Ensure scrollbar is updated when control becomes visible
                Reflow();
                ReflowSoon();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            Rectangle clip = e.ClipRectangle;

            // Apply scroll transform to everything
            e.Graphics.TranslateTransform(0, -_scrollOffset);

            foreach (var it in _items)
            {
                Rectangle r = it.Bounds;
                if (r.Bottom < _scrollOffset - GapBetweenBubbles) continue;
                if (r.Top > _scrollOffset + ClientSize.Height) break;

                DrawBubble(e.Graphics, it);
            }

            // Reset transform
            e.Graphics.ResetTransform();
            base.OnPaint(e);
        }

        private void DrawBubble(Graphics g, MessageItem it)
        {
            Rectangle r = it.Bounds;

            Color back, border;
            if (it.Role == MessageRole.User) { back = _clrUserBack; border = _clrUserBorder; }
            else if (it.Role == MessageRole.Assistant) { back = _clrAsstBack; border = _clrAsstBorder; }
            else { back = _clrSysBack; border = _clrSysBorder; }

            using (var path = RoundedRect(r, BubbleRadius))
            using (var b = new SolidBrush(back))
            using (var pen = new Pen(border))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }

            // Content area
            Rectangle content = new Rectangle(r.X + BubblePadding, r.Y + BubblePadding, r.Width - 2 * BubblePadding, r.Height - 2 * BubblePadding);
            DrawBlocks(g, content, it.Blocks, it);

            // Draw attachment pills at the bottom of content area
            if (it.Attachments != null && it.Attachments.Count > 0)
            {
                DrawAttachmentPills(g, new Rectangle(content.X, r.Bottom - BubblePadding - MeasureAttachmentsHeight(it, content.Width), content.Width, MeasureAttachmentsHeight(it, content.Width)), it);
            }
        }

        private void DrawAttachmentPills(Graphics g, Rectangle bounds, MessageItem it)
        {
            if (it.Attachments == null || it.Attachments.Count == 0) return;
            if (it.AttachmentPillRects == null) it.AttachmentPillRects = new List<Rectangle>(); else it.AttachmentPillRects.Clear();
            int x = bounds.X;
            int y = bounds.Y;
            int maxW = bounds.Width;
            for (int i = 0; i < it.Attachments.Count; i++)
            {
                var af = it.Attachments[i];
                string name = af != null ? (af.FileName ?? "(file)") : "(file)";
                Size sz = TextRenderer.MeasureText(name, _baseFont, new Size(int.MaxValue / 4, int.MaxValue / 4), TextFormatFlags.NoPadding);
                int pillW = Math.Min(maxW, sz.Width + 16);
                int pillH = Math.Max(_baseFont.Height + 6, 18);
                if (x > bounds.X && x + pillW > bounds.Right)
                {
                    x = bounds.X; y += pillH + 4;
                }
                Rectangle pill = new Rectangle(x, y, pillW, pillH);

                bool hover = (_hoverAttachItem == it && _hoverAttachIndex == i);
                bool pressed = (_pressAttachItem == it && _pressAttachIndex == i);

                Color baseBack = _isDarkTheme ? Color.FromArgb(60, 62, 66) : Color.FromArgb(240, 240, 240);
                if (pressed) baseBack = _clrCopyPressed; else if (hover) baseBack = _clrCopyHover;
                using (var sb = new SolidBrush(baseBack))
                using (var pen = new Pen(_isDarkTheme ? _clrScrollThumbBorder : _clrCodeBorder))
                using (var path = RoundedRect(pill, 9))
                {
                    g.FillPath(sb, path); g.DrawPath(pen, path);
                }
                Rectangle textRect = new Rectangle(pill.X + 8, pill.Y + (pill.Height - _baseFont.Height) / 2, Math.Max(0, pill.Width - 10), _baseFont.Height);
                using (var brush = new SolidBrush(ForeColor))
                {
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        g.DrawString(name, _baseFont, brush, (PointF)textRect.Location, fmt);
                    }
                }
                it.AttachmentPillRects.Add(pill);
                x += pillW + 6;
            }
        }

        private void DrawBlocks(Graphics g, Rectangle bounds, List<Block> blocks, MessageItem owner)
        {
            int y = bounds.Y;
            int x0 = bounds.X;
            int maxWidth = bounds.Width;
            int codeIndex = 0; // index of code block within this message for scroll state
            // Maintain numbering counters across blocks so ordered lists continue through sublists
            var numberedCounters = new Dictionary<int, int>(); // key: indent level, value: last number emitted

            foreach (var blk in blocks)
            {
                if (blk.Type == BlockType.Heading)
                {
                    var h = (HeadingBlock)blk;
                    Font f = GetHeadingFont(h.Level);
                    y += DrawInlineParagraph(g, x0, y, maxWidth, h.Inlines, f);
                    y += 4;
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.Paragraph)
                {
                    var p = (ParagraphBlock)blk;
                    y += DrawInlineParagraph(g, x0, y, maxWidth, p.Inlines, _baseFont);
                    y += 2;
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.BulletList)
                {
                    var list = (BulletListBlock)blk;
                    foreach (var item in list.Items)
                    {
                        int indentX = x0 + (item.IndentLevel * BulletIndent);
                        // bullet glyph
                        using (var b = new SolidBrush(ForeColor))
                        {
                            // simple bullet - different styles for different nesting levels
                            if (item.IndentLevel == 0)
                                g.FillEllipse(b, indentX, y + _baseFont.Height / 2 - 2, 4, 4);
                            else if (item.IndentLevel == 1)
                            {
                                using (var pen = new Pen(ForeColor))
                                    g.DrawEllipse(pen, indentX, y + _baseFont.Height / 2 - 2, 4, 4);
                            }
                            else
                                g.FillRectangle(b, indentX, y + _baseFont.Height / 2 - 1, 3, 3);
                        }
                        int textX = indentX + BulletIndent;
                        int used = DrawInlineParagraph(g, textX, y, maxWidth - (textX - x0), item.Content, _baseFont);
                        y += Math.Max(used, _baseFont.Height) + 2;
                    }
                }
                else if (blk.Type == BlockType.NumberedList)
                {
                    var list = (NumberedListBlock)blk;
                    foreach (var item in list.Items)
                    {
                        // determine sequential number based on indent level, continuing across blocks
                        int indent = item.IndentLevel;
                        // drop deeper levels if indent decreased
                        if (numberedCounters.Count > 0)
                        {
                            var toRemove = new List<int>();
                            foreach (var k in numberedCounters.Keys) if (k > indent) toRemove.Add(k);
                            for (int r = 0; r < toRemove.Count; r++) numberedCounters.Remove(toRemove[r]);
                        }
                        int prev;
                        if (!numberedCounters.TryGetValue(indent, out prev)) prev = 0;
                        int itemNumber = prev + 1;
                        numberedCounters[indent] = itemNumber;
                        int indentX = x0 + (item.IndentLevel * BulletIndent);
                        // number
                        string numberText = itemNumber.ToString() + ".";
                        Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                        using (var brush = new SolidBrush(ForeColor))
                        {
                            g.DrawString(numberText, _baseFont, brush, indentX, y);
                        }

                        int textX = indentX + numberSize.Width + 4; // 4px gap after number
                        int used = DrawInlineParagraph(g, textX, y, maxWidth - (textX - x0), item.Content, _baseFont);
                        y += Math.Max(used, _baseFont.Height) + 2;
                    }
                }
                else if (blk.Type == BlockType.CodeBlock)
                {
                    var c = (CodeBlock)blk;
                    // Colored segments and content size without wrapping
                    SyntaxHighlightingRenderer.EnqueueHighlight(c.Language, _isDarkTheme, c.Text, _monoFont);
                    var coloredSegments = SyntaxHighlightingRenderer.GetColoredSegments(c.Text, c.Language, _monoFont, _isDarkTheme);
                    Size contentNoWrap = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, coloredSegments);
                    int viewportW = Math.Max(0, maxWidth - 2 * CodeBlockPadding);
                    bool needH = contentNoWrap.Width > viewportW;
                    int boxW = Math.Min(maxWidth, Math.Max(0, Math.Min(contentNoWrap.Width + 2 * CodeBlockPadding, maxWidth)));
                    int textHeight = Math.Max(_monoFont.Height, contentNoWrap.Height);
                    int headerH = GetCodeHeaderHeight();
                    int boxH = textHeight + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                    Rectangle box = new Rectangle(x0, y, boxW, boxH);

                    // Draw code block background and border
                    using (var sb = new SolidBrush(_clrCodeBack))
                    using (var pen = new Pen(_clrCodeBorder))
                    {
                        g.FillRectangle(sb, box);
                        g.DrawRectangle(pen, box);
                    }

                    // Header area top (flush with top border to remove extra spacing)
                    int headerTop = box.Top;

                    // Copy button (top-right)
                    string copyText = "Copy";
                    SizeF copySizeF;
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        copySizeF = g.MeasureString(copyText, _baseFont, PointF.Empty, fmt);
                    }
                    int copyW = (int)Math.Ceiling(copySizeF.Width) + CodeCopyButtonPad * 2;
                    int copyH = headerH; // occupy full header height so top/bottom are flush
                    Rectangle copyRect = new Rectangle(box.Right - CodeCopyButtonPad - copyW, headerTop, copyW, copyH);

                    bool hoverCopy = (_hoverCopyItem == owner && _hoverCopyCodeIndex == codeIndex);
                    // Draw copy background on hover or mouse down
                    if (hoverCopy || (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex))
                    {
                        bool pressed = (owner == _copyPressedItem && codeIndex == _copyPressedCodeIndex);
                        using (var sb = new SolidBrush(pressed ? _clrCopyPressed : _clrCopyHover))
                        using (var pen = new Pen(_clrCodeBorder))
                        {
                            g.FillRectangle(sb, copyRect);
                            g.DrawRectangle(pen, copyRect);
                        }
                    }
                    // Draw copy text
                    using (var brush = new SolidBrush(_clrLink))
                    using (var fmt = StringFormat.GenericTypographic)
                    {
                        fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        var textPt = new PointF(copyRect.X + CodeCopyButtonPad, copyRect.Y + (copyRect.Height - _baseFont.Height) / 2f);
                        g.DrawString(copyText, _baseFont, brush, textPt, fmt);
                    }

                    // Language label (top-left)
                    string langLabel = c.Language;
                    if (!string.IsNullOrEmpty(langLabel))
                    {
                        using (var brush = new SolidBrush(ForeColor))
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            var labelFont = _boldFont ?? _baseFont;
                            var langPt = new PointF(box.Left + CodeCopyButtonPad, headerTop + (headerH - labelFont.Height) / 2f);
                            g.DrawString(langLabel, labelFont, brush, langPt, fmt);
                        }
                    }

                    // Header separator line
                    using (var pen = new Pen(_clrCodeBorder))
                    {
                        int headerBottom = headerTop + headerH;
                        g.DrawLine(pen, box.Left + CodeBlockPadding, headerBottom, box.Right - CodeBlockPadding, headerBottom);
                    }

                    // Text viewport
                    Rectangle textRect = new Rectangle(box.X + CodeBlockPadding, headerTop + headerH, box.Width - 2 * CodeBlockPadding, textHeight);

                    // Horizontal scrollbar geometry
                    int scrollX = 0;
                    if (owner.CodeScroll != null && codeIndex < owner.CodeScroll.Count)
                        scrollX = owner.CodeScroll[codeIndex];
                    int maxScroll = Math.Max(0, contentNoWrap.Width - textRect.Width);
                    if (scrollX > maxScroll) scrollX = maxScroll;
                    if (scrollX < 0) scrollX = 0;
                    if (owner.CodeScroll != null && codeIndex < owner.CodeScroll.Count)
                        owner.CodeScroll[codeIndex] = scrollX;

                    // Draw text without wrapping with horizontal scroll
                    SyntaxHighlightingRenderer.DrawColoredSegmentsNoWrap(g, coloredSegments, textRect, scrollX);

                    // Draw horizontal scrollbar if needed
                    if (needH && textRect.Width > 0)
                    {
                        Rectangle track = new Rectangle(textRect.X, textRect.Bottom + 2, textRect.Width, CodeHScrollHeight - 4);
                        bool hoverScroll = (_hoverScrollItem == owner && _hoverScrollCodeIndex == codeIndex);
                        Color trackBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollTrackBorder;
                        Color thumbBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollThumbBorder;
                        using (var trackBrush = new SolidBrush(_clrScrollTrack))
                        using (var trackPen = new Pen(trackBorder))
                        {
                            g.FillRectangle(trackBrush, track);
                            g.DrawRectangle(trackPen, track);
                        }

                        int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * textRect.Width / Math.Max(1, contentNoWrap.Width)));
                        int trackRange = Math.Max(1, track.Width - thumbW);
                        int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                        Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                        using (var thumbBrush = new SolidBrush(_clrScrollThumb))
                        using (var thumbPen = new Pen(thumbBorder))
                        {
                            g.FillRectangle(thumbBrush, thumb);
                            g.DrawRectangle(thumbPen, thumb);
                        }
                    }

                    y += box.Height + 4;
                    codeIndex++;
                    // Reset numbering when leaving list context
                    numberedCounters.Clear();
                }
                else if (blk.Type == BlockType.Table)
                {
                    var t = (TableBlock)blk;
                    int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                    if (cols > 0)
                    {
                        int cellPad = 6;
                        int border = 1;
                        // compute column widths by measuring unbounded to get intrinsic content width
                        int[] colWidths = new int[cols];
                        int headerH = 0;
                        for (int c = 0; c < cols; c++)
                        {
                            var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                            Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                            colWidths[c] = Math.Max(colWidths[c], sz.Width);
                            headerH = Math.Max(headerH, sz.Height);
                        }
                        int[] rowHeights = new int[t.Rows.Count];
                        for (int r = 0; r < t.Rows.Count; r++)
                        {
                            int rowH = 0;
                            for (int c = 0; c < cols; c++)
                            {
                                var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                rowH = Math.Max(rowH, sz.Height);
                            }
                            rowHeights[r] = rowH;
                        }

                        int intrinsicW = border; for (int c = 0; c < cols; c++) intrinsicW += colWidths[c] + cellPad * 2 + border;
                        int viewportW = Math.Max(0, maxWidth);
                        bool needH = intrinsicW > viewportW;
                        int viewW = Math.Min(viewportW, intrinsicW);

                        // Horizontal scroll state (per table)
                        if (owner.TableScroll == null) owner.TableScroll = new List<int>();
                        int tableIndex = 0; // compute table index within this message
                        for (int bi = 0; bi < blocks.Count && !object.ReferenceEquals(blocks[bi], blk); bi++)
                            if (blocks[bi].Type == BlockType.Table) tableIndex++;
                        while (owner.TableScroll.Count <= tableIndex) owner.TableScroll.Add(0);
                        int scrollX = owner.TableScroll[tableIndex];
                        int maxScroll = Math.Max(0, intrinsicW - viewW);
                        if (scrollX < 0) scrollX = 0; if (scrollX > maxScroll) scrollX = maxScroll;
                        owner.TableScroll[tableIndex] = scrollX;

                        // Draw table within a clipped viewport with horizontal offset
                        Rectangle tableViewport = new Rectangle(x0, y, viewW, headerH + cellPad * 2 + 1); // header first; body will extend
                        // compute full table height
                        int tableH = 1 + headerH + cellPad * 2 + 1; for (int r = 0; r < rowHeights.Length; r++) tableH += rowHeights[r] + cellPad * 2 + 1;
                        tableViewport.Height = tableH;

                        Region prevClip = g.Clip;
                        g.SetClip(tableViewport);

                        using (var pen = new Pen(_clrCodeBorder))
                        using (var headerBrush = new SolidBrush(_clrCodeBack))
                        using (var cellBrush = new SolidBrush(BackColor))
                        {
                            int drawX = x0 - scrollX;

                            // Header row background across viewport
                            int headerY = y;
                            int headerHeight = headerH + cellPad * 2;
                            g.FillRectangle(headerBrush, new Rectangle(x0, headerY, viewW, headerHeight + 1));

                            // Draw header cells
                            int x = drawX + 1; // start after left border
                            for (int c = 0; c < cols; c++)
                            {
                                Rectangle cellRect = new Rectangle(x, headerY + 1, colWidths[c] + cellPad * 2, headerHeight);
                                int textX = cellRect.X + cellPad;
                                int avail = colWidths[c];
                                var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                                DrawInlineParagraph(g, textX, cellRect.Y + (cellRect.Height - _baseFont.Height) / 2, avail, inl, _baseFont);
                                g.DrawRectangle(pen, new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 1, cellRect.Height + 1));
                                x += cellRect.Width + 1;
                            }
                            int yBody = headerY + headerHeight + 1;
                            // Body rows
                            for (int r = 0; r < t.Rows.Count; r++)
                            {
                                int rowH = rowHeights[r];
                                x = drawX + 1;
                                for (int c = 0; c < cols; c++)
                                {
                                    Rectangle cellRect = new Rectangle(x, yBody, colWidths[c] + cellPad * 2, rowH + cellPad * 2);
                                    g.FillRectangle(cellBrush, cellRect);
                                    int textX = cellRect.X + cellPad;
                                    int avail = colWidths[c];
                                    var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                    DrawInlineParagraph(g, textX, cellRect.Y + cellPad, avail, inl, _baseFont);
                                    g.DrawRectangle(pen, new Rectangle(cellRect.X - 1, cellRect.Y - 1, cellRect.Width + 1, cellRect.Height + 1));
                                    x += cellRect.Width + 1;
                                }
                                yBody += rowH + cellPad * 2 + 1;
                            }
                        }

                        g.Clip = prevClip;

                        // Draw horizontal scrollbar if needed
                        if (needH && viewW > 0)
                        {
                            Rectangle track = new Rectangle(x0, y + tableH + 2, viewW, CodeHScrollHeight - 4);
                            bool hoverScroll = (_hoverScrollItem == owner && _hoverScrollIsTable && _hoverScrollTableIndex == tableIndex);
                            Color trackBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollTrackBorder;
                            Color thumbBorder = hoverScroll ? _clrScrollThumbBorder : _clrScrollThumbBorder;
                            using (var trackBrush = new SolidBrush(_clrScrollTrack))
                            using (var trackPen = new Pen(trackBorder))
                            {
                                g.FillRectangle(trackBrush, track);
                                g.DrawRectangle(trackPen, track);
                            }
                            int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * viewW / Math.Max(1, intrinsicW)));
                            int trackRange = Math.Max(1, track.Width - thumbW);
                            int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                            Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                            using (var thumbBrush = new SolidBrush(_clrScrollThumb))
                            using (var thumbPen = new Pen(thumbBorder))
                            {
                                g.FillRectangle(thumbBrush, thumb);
                                g.DrawRectangle(thumbPen, thumb);
                            }
                        }
                        y += tableH + (needH ? CodeHScrollHeight : 0);
                    }
                    y += 2; // small gap after table
                }
            }
        }

        private int DrawInlineParagraph(Graphics g, int x, int y, int maxWidth, List<InlineRun> runs, Font baseFont)
        {
            int xCursor = x;
            int yCursor = y;
            int lineHeight = baseFont.Height;
            int lineWidth = 0;

            // Collect segments for processing
            var segments = new List<LayoutSeg>();
            foreach (var seg in WordWrapRuns(runs, baseFont, maxWidth))
            {
                segments.Add(seg);
            }

            // Draw inline code backgrounds first (for each line)
            int currentLine = 0;
            int lineStartY = yCursor;
            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];

                if (seg.IsNewLine)
                {
                    // Process inline code backgrounds for the current line
                    DrawInlineCodeBackgrounds(g, segments, currentLine, i, x, lineStartY);

                    lineStartY += lineHeight + 2;
                    currentLine = i + 1;
                    lineHeight = baseFont.Height;
                    continue;
                }

                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }

            // Process the last line
            if (currentLine < segments.Count)
            {
                DrawInlineCodeBackgrounds(g, segments, currentLine, segments.Count, x, lineStartY);
            }

            // Now draw the text
            xCursor = x;
            yCursor = y;
            lineHeight = baseFont.Height;

            foreach (var seg in segments)
            {
                if (seg.IsNewLine)
                {
                    yCursor += lineHeight + 2;
                    xCursor = x;
                    lineWidth = 0;
                    lineHeight = baseFont.Height;
                    continue;
                }

                Rectangle r = new Rectangle(xCursor, yCursor, seg.Rect.Width, seg.Rect.Height);

                if (seg.IsLink)
                {
                    using (var brush = new SolidBrush(_clrLink)) // link color per theme
                    {
                        // Draw text
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            g.DrawString(seg.Text, seg.Font, brush, (PointF)r.Location, fmt);
                            SizeF w = g.MeasureString(seg.Text, seg.Font, PointF.Empty, fmt);
                            int underlineWidth = (int)Math.Ceiling(w.Width);
                            int underlineY = r.Bottom - 2;
                            using (var pen = new Pen(brush.Color))
                            {
                                g.DrawLine(pen, r.Left, underlineY, r.Left + underlineWidth, underlineY);
                            }
                        }
                    }
                }
                else
                {
                    using (var brush = new SolidBrush(ForeColor))
                    {
                        using (var fmt = StringFormat.GenericTypographic)
                        {
                            fmt.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                            g.DrawString(seg.Text, seg.Font, brush, (PointF)r.Location, fmt);
                        }
                    }
                }
                xCursor += r.Width;
                lineWidth += r.Width;
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }

            // last line height
            yCursor += lineHeight + 2;
            return yCursor - y;
        }

        private int MeasureInlineParagraphHeight(Graphics g, int maxWidth, List<InlineRun> runs, Font baseFont)
        {
            int lineHeight = baseFont.Height;
            int total = 0;
            foreach (var seg in WordWrapRuns(runs, baseFont, maxWidth))
            {
                if (seg.IsNewLine)
                {
                    total += lineHeight + 2;
                    lineHeight = baseFont.Height;
                    continue;
                }
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }
            total += lineHeight + 2;
            return total;
        }

        private void DrawInlineCodeBackgrounds(Graphics g, List<LayoutSeg> segments, int lineStart, int lineEnd, int x, int y)
        {
            int currentX = x;

            for (int i = lineStart; i < lineEnd; i++)
            {
                var seg = segments[i];

                if (seg.IsInlineCode)
                {
                    // Find the extent of consecutive inline code segments
                    int startX = currentX;
                    int endX = currentX + seg.Rect.Width;
                    int j = i + 1;

                    // Look ahead for more inline code segments
                    while (j < lineEnd && segments[j].IsInlineCode)
                    {
                        endX += segments[j].Rect.Width;
                        j++;
                    }

                    // Draw background for the entire inline code run
                    Rectangle bg = new Rectangle(
                        startX - InlineCodePaddingX,
                        y - InlineCodePaddingY,
                        endX - startX + 2 * InlineCodePaddingX,
                        seg.Font.Height + 2 * InlineCodePaddingY);

                    using (var sb = new SolidBrush(_clrInlineCodeBack))
                    using (var pen = new Pen(_clrInlineCodeBorder))
                    {
                        g.FillRectangle(sb, bg);
                        g.DrawRectangle(pen, bg);
                    }

                    // Skip the segments we just processed
                    i = j - 1; // -1 because the loop will increment
                }

                if (i < lineEnd)
                    currentX += segments[i].Rect.Width;
            }
        }

        private Font GetHeadingFont(int level)
        {
            switch (level)
            {
                case 1: return _h1;
                case 2: return _h2;
                case 3: return _h3;
                case 4: return _h4;
                case 5: return _h5;
                default: return _h6;
            }
        }

        // ---------- Scrolling & input ----------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);

            // If Shift is pressed and we're hovering a horizontally scrollable code block or table,
            // apply wheel to horizontal scroll instead of the transcript vertical scrollbar.
            try
            {
                if ((Control.ModifierKeys & Keys.Shift) == Keys.Shift)
                {
                    var ui = HitTestCodeUI(e.Location);
                    if (ui.Hit && ui.ContentWidth > ui.ViewportWidth && ui.Item != null)
                    {
                        int hStep = Math.Max(16, ScrollStep); // horizontal step per wheel notch
                        int deltaX = (int)System.Math.Round(-(e.Delta / 120.0) * hStep, MidpointRounding.AwayFromZero);
                        if (ui.IsTable)
                        {
                            int idx = ui.TableIndex;
                            if (ui.Item.TableScroll == null) ui.Item.TableScroll = new List<int>();
                            while (ui.Item.TableScroll.Count <= idx) ui.Item.TableScroll.Add(0);
                            int current = ui.Item.TableScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.TableScroll[idx] = next;
                            Invalidate();
                            return; // handled
                        }
                        else if (ui.CodeIndex >= 0)
                        {
                            int idx = ui.CodeIndex;
                            if (ui.Item.CodeScroll == null) ui.Item.CodeScroll = new List<int>();
                            while (ui.Item.CodeScroll.Count <= idx) ui.Item.CodeScroll.Add(0);
                            int current = ui.Item.CodeScroll[idx];
                            int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                            int next = Math.Max(0, Math.Min(maxScroll, current + deltaX));
                            ui.Item.CodeScroll[idx] = next;
                            Invalidate();
                            return; // handled
                        }
                    }
                }
            }
            catch { }

            // Fallback to vertical scroll using proportional precision handling
            ScrollByWheelDelta(e.Delta);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_draggingHScroll)
            {
                _draggingHScroll = false; _dragScrollItem = null; _dragScrollCodeIndex = -1; _dragScrollIsTable = false; Capture = false; Invalidate();
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                _ctxHit = HitTest(e.Location);
                if (_ctxHit != null)
                {
                    try
                    {
                        // Rebuild context menu items for this hit
                        _ctx.Items.Clear();
                        _ctx.Items.Add("Copy", null, delegate { if (_ctxHit != null) SafeClipboardSetText(_ctxHit.RawMarkdown ?? string.Empty); });
                        if (_ctxHit.Role == MessageRole.User)
                        {
                            _ctx.Items.Add("Edit...", null, delegate
                            {
                                try
                                {
                                    int idx = IndexOfMessageItem(_ctxHit);
                                    if (idx >= 0)
                                    {
                                        var handler = UserMessageEditRequested;
                                        if (handler != null) handler(idx, _ctxHit.RawMarkdown ?? string.Empty);
                                    }
                                }
                                catch { }
                            });
                        }
                    }
                    catch { }

                    _ctx.Show(this, e.Location);
                }
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                // Attachment pill click
                var pill = HitTestAttachmentPill(e.Location);
                if (pill.Item != null && pill.Index >= 0)
                {
                    _pressAttachItem = null; _pressAttachIndex = -1; Invalidate();
                    OpenAttachmentInViewer(pill.Item, pill.Index);
                    return;
                }
                // Copy button click
                var ui = HitTestCodeUI(e.Location);
                if (ui.Hit && ui.Which == CodeUiHit.CopyButton && ui.Item != null)
                {
                    var cb = (CodeBlock)ui.Block;
                    // Normalize newlines to CRLF for Windows clipboard
                    string text = cb != null ? cb.Text : string.Empty;
                    if (text == null) text = string.Empty;
                    string normalized = text.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                    SafeClipboardSetText(normalized);
                    _copyPressedItem = null; _copyPressedCodeIndex = -1;
                    Invalidate();
                    return;
                }
                // Clear any pressed copy state on mouse up
                if (_copyPressedItem != null)
                { _copyPressedItem = null; _copyPressedCodeIndex = -1; Invalidate(); }
                // Link click detection
                string link = HitTestLink(e.Location);
                if (!string.IsNullOrEmpty(link))
                {
                    try
                    {
                        string supermiumPath = @"C:\\Program Files\\Supermium\\chrome.exe";
                        if (IsWindowsXP() && File.Exists(supermiumPath))
                        {
                            // On XP (no reliable default browser override), prefer Supermium if installed
                            System.Diagnostics.Process.Start(supermiumPath, link);
                        }
                        else
                        {
                            // On newer OS (or if Supermium absent), let shell pick the default handler
                            System.Diagnostics.Process.Start(link);
                        }
                    }
                    catch { }
                }
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                var pill = HitTestAttachmentPill(e.Location);
                if (pill.Item != null && pill.Index >= 0)
                {
                    _pressAttachItem = pill.Item; _pressAttachIndex = pill.Index; Invalidate();
                    return;
                }
                var ui = HitTestCodeUI(e.Location);
                if (ui.Hit)
                {
                    if (ui.Which == CodeUiHit.CopyButton)
                    {
                        _copyPressedItem = ui.Item;
                        _copyPressedCodeIndex = ui.CodeIndex;
                        Invalidate();
                        return;
                    }
                    if (ui.Which == CodeUiHit.ScrollThumb)
                    {
                        _draggingHScroll = true;
                        _dragScrollItem = ui.Item;
                        _dragScrollCodeIndex = ui.IsTable ? -1 : ui.CodeIndex;
                        _dragScrollIsTable = ui.IsTable;
                        _dragScrollTrackRect = ui.ScrollTrackRect;
                        _dragScrollContentWidth = ui.ContentWidth;
                        _dragScrollViewportWidth = ui.ViewportWidth;
                        _dragStartMouseX = e.X;
                        _dragStartScrollX = ui.IsTable ? ui.Item.TableScroll[ui.TableIndex] : ui.Item.CodeScroll[ui.CodeIndex];
                        Capture = true;
                        return;
                    }
                    if (ui.Which == CodeUiHit.ScrollTrack)
                    {
                        // Jump to position where thumb center aligns with click
                        int trackWidth = Math.Max(1, ui.ScrollTrackRect.Width);
                        int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)trackWidth * ui.ViewportWidth / Math.Max(1, ui.ContentWidth)));
                        int trackRange = Math.Max(1, trackWidth - thumbW);
                        int clickOffset = Math.Max(0, Math.Min(trackRange, e.X - ui.ScrollTrackRect.X - thumbW / 2));
                        int maxScroll = Math.Max(0, ui.ContentWidth - ui.ViewportWidth);
                        int newScroll = (int)Math.Round((double)clickOffset / trackRange * maxScroll);
                        if (ui.IsTable) ui.Item.TableScroll[ui.TableIndex] = newScroll; else ui.Item.CodeScroll[ui.CodeIndex] = newScroll;
                        Invalidate();
                        return;
                    }
                }
            }
        }

        private static bool IsWindowsXP()
        {
            try
            {
                OperatingSystem os = Environment.OSVersion;
                if (os.Platform == PlatformID.Win32NT && os.Version.Major == 5)
                {
                    // 5.1 = XP, 5.2 = XP x64 / Server 2003
                    return os.Version.Minor == 1 || os.Version.Minor == 2;
                }
            }
            catch { }
            return false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_draggingHScroll && _dragScrollItem != null)
            {
                // update scroll based on mouse delta
                int dx = e.X - _dragStartMouseX;
                int trackWidth = Math.Max(1, _dragScrollTrackRect.Width);
                int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)trackWidth * _dragScrollViewportWidth / Math.Max(1, _dragScrollContentWidth)));
                int trackRange = Math.Max(1, trackWidth - thumbW);
                int maxScroll = Math.Max(0, _dragScrollContentWidth - _dragScrollViewportWidth);
                int deltaScroll = (int)Math.Round((double)dx / trackRange * maxScroll);
                int newScroll = Math.Max(0, Math.Min(maxScroll, _dragStartScrollX + deltaScroll));
                if (_dragScrollIsTable)
                {
                    // Find current table index via hit test again or approximate: compute by geometry would be heavy; reuse start scroll index mapping
                    // We'll adjust both code path: On mouse down stored _dragScrollTrackRect and widths; here only need to update the same target list.
                    // Use the closest matching thumb width mapping: we piggybacked _dragStartScrollX from the appropriate list earlier.
                    // For table, we must identify index; we can recompute using HitTestCodeUI at the current mouse to find which table scrollbar is under drag.
                    var tableUi = HitTestCodeUI(new Point(e.X, e.Y));
                    if (tableUi.Hit && tableUi.IsTable) { _dragScrollItem.TableScroll[tableUi.TableIndex] = newScroll; }
                    else { if (_dragScrollItem.TableScroll != null && _dragScrollItem.TableScroll.Count > 0) _dragScrollItem.TableScroll[0] = newScroll; }
                }
                else
                {
                    if (_dragScrollCodeIndex >= 0 && _dragScrollCodeIndex < _dragScrollItem.CodeScroll.Count)
                        _dragScrollItem.CodeScroll[_dragScrollCodeIndex] = newScroll;
                }
                Invalidate();
                return;
            }

            // Hover check for copy button and set cursor
            var ui = HitTestCodeUI(e.Location);
            bool overInteractive = false;
            if (ui.Hit && (ui.Which == CodeUiHit.CopyButton || ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack))
            {
                overInteractive = true;
                if (ui.Which == CodeUiHit.CopyButton)
                {
                    _hoverCopyItem = ui.Item; _hoverCopyCodeIndex = ui.CodeIndex;
                }
                if (ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack)
                {
                    _hoverScrollItem = ui.Item; _hoverScrollCodeIndex = ui.CodeIndex; _hoverScrollIsTable = ui.IsTable; _hoverScrollTableIndex = ui.TableIndex;
                }
            }
            else
            {
                _hoverCopyItem = null; _hoverCopyCodeIndex = -1;
                _hoverScrollItem = null; _hoverScrollCodeIndex = -1; _hoverScrollIsTable = false; _hoverScrollTableIndex = -1;
            }

            // Hover over attachment pill
            var pillHit = HitTestAttachmentPill(e.Location);
            if (pillHit.Item != null)
            {
                overInteractive = true; _hoverAttachItem = pillHit.Item; _hoverAttachIndex = pillHit.Index; Cursor = Cursors.Hand; Invalidate(); return;
            }
            else
            {
                _hoverAttachItem = null; _hoverAttachIndex = -1;
            }

            string link = HitTestLink(e.Location);
            if (!string.IsNullOrEmpty(link)) { overInteractive = true; Cursor = Cursors.Hand; return; }

            if (overInteractive)
            {
                // Use standard cursor for scroll bars; hand for copy
                if (ui.Which == CodeUiHit.ScrollThumb || ui.Which == CodeUiHit.ScrollTrack) Cursor = Cursors.Default;
                else if (ui.Which == CodeUiHit.CopyButton) Cursor = Cursors.Hand;
                else Cursor = Cursors.Default;
            }
            else
            {
                Cursor = Cursors.Default;
            }
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverCopyItem = null; _hoverCopyCodeIndex = -1;
            _hoverScrollItem = null; _hoverScrollCodeIndex = -1;
            _copyPressedItem = null; _copyPressedCodeIndex = -1;
            _hoverAttachItem = null; _hoverAttachIndex = -1; _pressAttachItem = null; _pressAttachIndex = -1;
            Invalidate();
        }

        private struct PillHit
        {
            public MessageItem Item; public int Index;
        }
        private PillHit HitTestAttachmentPill(Point clientPt)
        {
            PillHit ph = new PillHit { Item = null, Index = -1 };
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (it.AttachmentPillRects == null || it.AttachmentPillRects.Count == 0) continue;
                for (int i = 0; i < it.AttachmentPillRects.Count; i++)
                {
                    var r = it.AttachmentPillRects[i];
                    if (r.Contains(virt)) { ph.Item = it; ph.Index = i; return ph; }
                }
            }
            return ph;
        }

        private void OpenAttachmentInViewer(MessageItem it, int index)
        {
            try
            {
                if (it == null || it.Attachments == null || index < 0 || index >= it.Attachments.Count) return;
                var af = it.Attachments[index]; if (af == null) return;
                using (var dlg = new FileViewerForm())
                {
                    var rtbField = typeof(FileViewerForm).GetField("rtbFileText", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                    var rtb = rtbField != null ? (RichTextBox)rtbField.GetValue(dlg) : null;
                    if (rtb != null)
                    {
                        rtb.Text = af.Content ?? string.Empty;

                        // Determine theme
                        bool dark = false;
                        try
                        {
                            string theme = AppSettings.GetString("theme");
                            dark = !string.IsNullOrEmpty(theme) && theme.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                        }
                        catch { dark = false; }

                        // Apply themed background/foreground to match chat
                        if (dark)
                        {
                            rtb.BackColor = Color.FromArgb(0x24, 0x27, 0x3A); // Macchiato Base
                            rtb.ForeColor = Color.FromArgb(230, 230, 230);
                        }
                        else
                        {
                            rtb.BackColor = SystemColors.Window;
                            rtb.ForeColor = SystemColors.WindowText;
                        }

                        string lang = GetFileExtension(af.FileName);
                        try { RichTextBoxSyntaxHighlighter.Highlight(rtb, lang, dark); }
                        catch { }
                    }
                    dlg.Text = af.FileName ?? "Attachment";
                    dlg.StartPosition = FormStartPosition.CenterParent;
                    dlg.ShowDialog(FindForm());
                }
            }
            catch { }
        }

        private string GetFileExtension(string fileName)
        {
            try
            {
                if (string.IsNullOrEmpty(fileName)) return null;
                string ext = Path.GetExtension(fileName);
                if (string.IsNullOrEmpty(ext)) return null;
                return ext.TrimStart('.').ToLowerInvariant();
            }
            catch { }
            return null;
        }

        private MessageItem HitTest(Point clientPt)
        {
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i].Bounds.Contains(virt))
                    return _items[i];
            }
            return null;
        }

        private string HitTestLink(Point clientPt)
        {
            // Determine which message
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);
            foreach (var it in _items)
            {
                if (!it.Bounds.Contains(virt)) continue;
                // Re-layout its blocks to locate link rectangles (simple approach; could cache)
                int contentX = it.Bounds.X + BubblePadding;
                int contentY = it.Bounds.Y + BubblePadding;
                int contentW = it.Bounds.Width - 2 * BubblePadding;
                int y = contentY;
                foreach (var blk in it.Blocks)
                {
                    if (blk.Type == BlockType.Paragraph || blk.Type == BlockType.Heading)
                    {
                        List<InlineRun> inlines;
                        Font baseFont;
                        if (blk.Type == BlockType.Heading)
                        {
                            var h = (HeadingBlock)blk;
                            inlines = h.Inlines;
                            baseFont = GetHeadingFont(h.Level);
                        }
                        else
                        {
                            var p = (ParagraphBlock)blk;
                            inlines = p.Inlines;
                            baseFont = _baseFont;
                        }
                        int x0 = contentX;
                        int lineHeight = baseFont.Height;
                        int xCursor = x0;
                        var wrapped = new List<LayoutSeg>();
                        foreach (var seg in WordWrapRuns(inlines, baseFont, contentW)) wrapped.Add(seg);
                        int yCursor = y;
                        foreach (var seg in wrapped)
                        {
                            if (seg.IsNewLine)
                            {
                                yCursor += lineHeight + 2;
                                xCursor = x0;
                                lineHeight = baseFont.Height;
                                continue;
                            }
                            Rectangle r = new Rectangle(xCursor, yCursor, seg.Rect.Width, seg.Rect.Height);
                            if (seg.IsLink && r.Contains(virt)) return seg.LinkUrl;
                            xCursor += r.Width;
                            lineHeight = Math.Max(lineHeight, seg.Font.Height);
                        }
                        y = yCursor + lineHeight + 2 + 2; // account for added gaps
                    }
                    else if (blk.Type == BlockType.CodeBlock)
                    {
                        // skip; links not in code blocks
                        // Advance y using the same calculation as drawing (no wrap, include copy header and optional h-scroll)
                        var cb = (CodeBlock)blk;
                        using (Graphics g = CreateGraphics())
                        {
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(cb.Text, cb.Language, _monoFont, _isDarkTheme);
                            Size contentNoWrap = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int viewportW = Math.Max(0, contentW - 2 * CodeBlockPadding);
                            bool needH = contentNoWrap.Width > viewportW;
                            int textH = Math.Max(_monoFont.Height, contentNoWrap.Height);
                            int headerH = GetCodeHeaderHeight();
                            int boxH = textH + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                            y += boxH + 4;
                        }
                    }
                    else if (blk.Type == BlockType.BulletList)
                    {
                        // Rough skip for bullets (no links expected yet)
                        var list = (BulletListBlock)blk;
                        foreach (var item in list.Items)
                        {
                            var wrapped = new List<LayoutSeg>();
                            foreach (var seg in WordWrapRuns(item.Content, _baseFont, contentW - BulletIndent)) wrapped.Add(seg);
                            int lineHeight = _baseFont.Height;
                            int xCursor = contentX + (item.IndentLevel * BulletIndent) + BulletIndent;
                            int yCursor = y;
                            foreach (var seg in wrapped)
                            {
                                if (seg.IsNewLine)
                                {
                                    yCursor += lineHeight + 2; xCursor = contentX + (item.IndentLevel * BulletIndent) + BulletIndent; lineHeight = _baseFont.Height; continue;
                                }
                                Rectangle r2 = new Rectangle(xCursor, yCursor, seg.Rect.Width, seg.Rect.Height);
                                if (seg.IsLink && r2.Contains(virt)) return seg.LinkUrl;
                                xCursor += r2.Width;
                                lineHeight = Math.Max(lineHeight, seg.Font.Height);
                            }
                            y = yCursor + lineHeight + 2 + 2;
                        }
                    }
                    else if (blk.Type == BlockType.NumberedList)
                    {
                        var list = (NumberedListBlock)blk;
                        int itemNo = 1;
                        foreach (var item in list.Items)
                        {
                            var wrapped = new List<LayoutSeg>();
                            foreach (var seg in WordWrapRuns(item.Content, _baseFont, contentW - BulletIndent)) wrapped.Add(seg);
                            int lineHeight = _baseFont.Height;
                            int xCursor = contentX + (item.IndentLevel * BulletIndent) + BulletIndent + 16; // approximate number width
                            int yCursor = y;
                            foreach (var seg in wrapped)
                            {
                                if (seg.IsNewLine)
                                { yCursor += lineHeight + 2; xCursor = contentX + (item.IndentLevel * BulletIndent) + BulletIndent + 16; lineHeight = _baseFont.Height; continue; }
                                Rectangle r2 = new Rectangle(xCursor, yCursor, seg.Rect.Width, seg.Rect.Height);
                                if (seg.IsLink && r2.Contains(virt)) return seg.LinkUrl;
                                xCursor += r2.Width;
                                lineHeight = Math.Max(lineHeight, seg.Font.Height);
                            }
                            y = yCursor + lineHeight + 2 + 2;
                            itemNo++;
                        }
                    }
                    else if (blk.Type == BlockType.Table)
                    {
                        // Advance y past table area (without link detection within table for now)
                        var t = (TableBlock)blk;
                        int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                        if (cols > 0)
                        {
                            int cellPad = 6; int border = 1;
                            int[] colWidths = new int[cols]; int headerH = 0;
                            for (int c = 0; c < cols; c++)
                            {
                                var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                                Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                headerH = Math.Max(headerH, sz.Height);
                            }
                            int[] rowHeights = new int[t.Rows.Count];
                            for (int r = 0; r < t.Rows.Count; r++)
                            {
                                int rowH = 0;
                                for (int c = 0; c < cols; c++)
                                {
                                    var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                    Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                    colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                    rowH = Math.Max(rowH, sz.Height);
                                }
                                rowHeights[r] = rowH;
                            }
                            int intrinsicW = border; for (int c = 0; c < cols; c++) intrinsicW += colWidths[c] + cellPad * 2 + border;
                            int tableH = 1 + headerH + cellPad * 2 + 1; for (int r = 0; r < rowHeights.Length; r++) tableH += rowHeights[r] + cellPad * 2 + 1;
                            bool needH = intrinsicW > contentW;
                            y += tableH + (needH ? CodeHScrollHeight : 0) + 2;
                        }
                    }
                }
            }
            return null;
        }

        private int IndexOfMessageItem(MessageItem it)
        {
            try
            {
                for (int i = 0; i < _items.Count; i++)
                {
                    if (object.ReferenceEquals(_items[i], it)) return i;
                }
            }
            catch { }
            return -1;
        }

        private void ScrollToBottom()
        {
            int view = Math.Max(0, ClientSize.Height);
            int max = Math.Max(0, _contentHeight - view);
            _scrollOffset = max;
            if (_vbar.Enabled)
            {
                int allowedMax = Math.Max(0, _vbar.Maximum - _vbar.LargeChange + 1);
                _vbar.Value = Math.Max(_vbar.Minimum, Math.Min(allowedMax, max));
            }
        }

        private void UpdateScrollbar()
        {
            int view = Math.Max(0, ClientSize.Height);
            int maxScrollOffset = Math.Max(0, _contentHeight - view);

            // If view is 0 (control not properly sized yet), disable scrollbar
            if (view <= 0)
            {
                _vbar.Enabled = false;
                _vbar.Minimum = 0;
                _vbar.Maximum = 0;
                _vbar.Value = 0;
                _scrollOffset = 0;
                return;
            }

            _vbar.Minimum = 0;
            _vbar.LargeChange = Math.Max(1, view);
            _vbar.SmallChange = ScrollStep;
            // For WinForms ScrollBar, Maximum should be totalContent-1; allowed Value max is Maximum-LargeChange+1
            _vbar.Maximum = Math.Max(0, _contentHeight - 1);
            int allowedMaxValue = Math.Max(0, _vbar.Maximum - _vbar.LargeChange + 1);

            _vbar.Enabled = maxScrollOffset > 0;

            // Stick to bottom during streaming
            if (_stickToBottom && _vbar.Enabled)
            {
                int desired = Math.Max(0, _contentHeight - view);
                _scrollOffset = desired;
                _vbar.Value = Math.Max(_vbar.Minimum, Math.Min(allowedMaxValue, desired));
                return;
            }

            // Clamp our logical scroll offset to what the scrollbar will accept
            _scrollOffset = Math.Max(0, Math.Min(allowedMaxValue, _scrollOffset));
            _vbar.Value = _scrollOffset;
        }

        // Post a deferred reflow/scrollbar refresh so measurements use the final viewport size
        private void ReflowSoon()
        {
            if (!IsHandleCreated) return;
            if (_reflowQueued) return;
            _reflowQueued = true;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    _reflowQueued = false;
                    Reflow();
                    Invalidate();
                });
            }
            catch { _reflowQueued = false; }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool StickToBottomDuringStreaming
        {
            get { return _stickToBottom; }
            set
            {
                if (_stickToBottom == value) return;
                _stickToBottom = value;
                UpdateScrollbar();
                Invalidate();
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            var gp = new System.Drawing.Drawing2D.GraphicsPath();
            gp.AddArc(r.X, r.Y, d, d, 180, 90);
            gp.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            gp.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            gp.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            gp.CloseFigure();
            return gp;
        }

        private static void SafeClipboardSetText(string s)
        {
            try
            {
                if (s == null) s = string.Empty;
                // Normalize line endings to CRLF for Windows clipboard
                string normalized = s.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\r\n");
                Clipboard.SetText(normalized);
            }
            catch { /* clipboard busy; ignore */ }
        }

        // --------- Helpers for code block UI hit testing ---------
        private enum CodeUiHit { None, CopyButton, ScrollThumb, ScrollTrack, Text }
        private struct CodeUiInfo
        {
            public bool Hit;
            public CodeUiHit Which;
            public MessageItem Item;
            public Block Block;
            public int CodeIndex;
            public Rectangle ScrollTrackRect;
            public int ContentWidth;
            public int ViewportWidth;
            public bool IsTable; // true when referring to a table scrollbar
            public int TableIndex; // when IsTable
        }

        private CodeUiInfo HitTestCodeUI(Point clientPt)
        {
            var info = new CodeUiInfo { Hit = false, Which = CodeUiHit.None };
            Point virt = new Point(clientPt.X, clientPt.Y + _scrollOffset);

            // Find containing message
            foreach (var it in _items)
            {
                if (!it.Bounds.Contains(virt)) continue;
                int contentX = it.Bounds.X + BubblePadding;
                int contentY = it.Bounds.Y + BubblePadding;
                int contentW = it.Bounds.Width - 2 * BubblePadding;
                int y = contentY;
                int codeIdx = 0;
                using (Graphics g = CreateGraphics())
                {
                    foreach (var blk in it.Blocks)
                    {
                        if (blk.Type == BlockType.Heading)
                        {
                            var h = (HeadingBlock)blk;
                            int used = MeasureInlineParagraphHeight(g, contentW, h.Inlines, GetHeadingFont(h.Level));
                            // Copy button not here; just spacing like DrawBlocks
                            y += used + 4;
                        }
                        else if (blk.Type == BlockType.Paragraph)
                        {
                            var p = (ParagraphBlock)blk;
                            int used = MeasureInlineParagraphHeight(g, contentW, p.Inlines, _baseFont);
                            y += used + 2;
                        }
                        else if (blk.Type == BlockType.BulletList)
                        {
                            var list = (BulletListBlock)blk;
                            foreach (var item in list.Items)
                            {
                                int indentX = contentX + (item.IndentLevel * BulletIndent);
                                int textX = indentX + BulletIndent;
                                int used = MeasureInlineParagraphHeight(g, contentW - (textX - contentX), item.Content, _baseFont);
                                y += Math.Max(used, _baseFont.Height) + 2;
                            }
                        }
                        else if (blk.Type == BlockType.NumberedList)
                        {
                            var list = (NumberedListBlock)blk;
                            // Maintain numbering counters for accurate width when numbers exceed one digit or continue across lists
                            var counters = new Dictionary<int, int>();
                            foreach (var item in list.Items)
                            {
                                int indent = item.IndentLevel;
                                if (counters.Count > 0)
                                {
                                    var toRemove = new List<int>();
                                    foreach (var k in counters.Keys) if (k > indent) toRemove.Add(k);
                                    for (int r = 0; r < toRemove.Count; r++) counters.Remove(toRemove[r]);
                                }
                                int prev; if (!counters.TryGetValue(indent, out prev)) prev = 0; int itemNumber = prev + 1; counters[indent] = itemNumber;
                                string numberText = itemNumber.ToString() + ".";
                                int indentX = contentX + (item.IndentLevel * BulletIndent);
                                Size numberSize = TextRenderer.MeasureText(numberText, _baseFont);
                                int textX = indentX + numberSize.Width + 4;
                                int used = MeasureInlineParagraphHeight(g, contentW - (textX - contentX), item.Content, _baseFont);
                                y += Math.Max(used, _baseFont.Height) + 2;
                            }
                        }
                        else if (blk.Type == BlockType.CodeBlock)
                        {
                            var cb = (CodeBlock)blk;
                            var colored = SyntaxHighlightingRenderer.GetColoredSegments(cb.Text, cb.Language, _monoFont, _isDarkTheme);
                            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                            int viewportW = Math.Max(0, contentW - 2 * CodeBlockPadding);
                            bool needH = content.Width > viewportW;
                            int textH = Math.Max(_monoFont.Height, content.Height);
                            int headerH = GetCodeHeaderHeight();
                            int boxH = textH + 2 * CodeBlockPadding + headerH + (needH ? CodeHScrollHeight : 0);
                            Rectangle box = new Rectangle(contentX, y, Math.Min(contentW, Math.Max(0, Math.Min(content.Width + 2 * CodeBlockPadding, contentW))), boxH);

                            // Copy button rect
                            SizeF copySizeF = g.MeasureString("Copy", _baseFont, PointF.Empty, StringFormat.GenericTypographic);
                            int copyW = (int)Math.Ceiling(copySizeF.Width) + CodeCopyButtonPad * 2;
                            int copyH = headerH;
                            int headerTop = box.Top;
                            Rectangle copyRect = new Rectangle(box.Right - CodeCopyButtonPad - copyW, headerTop, copyW, copyH);
                            if (copyRect.Contains(virt))
                            {
                                info.Hit = true; info.Which = CodeUiHit.CopyButton; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; return info;
                            }

                            // Scrollbar rects
                            Rectangle textRect = new Rectangle(box.X + CodeBlockPadding, headerTop + headerH, box.Width - 2 * CodeBlockPadding, textH);
                            // Report a generic Text hit when hovering over code content (for Shift+Wheel horizontal scroll)
                            if (textRect.Contains(virt))
                            {
                                info.Hit = true; info.Which = CodeUiHit.Text; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                            }
                            if (needH)
                            {
                                Rectangle track = new Rectangle(textRect.X, textRect.Bottom + 2, textRect.Width, CodeHScrollHeight - 4);
                                int maxScroll = Math.Max(0, content.Width - textRect.Width);
                                int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * textRect.Width / Math.Max(1, content.Width)));
                                int trackRange = Math.Max(1, track.Width - thumbW);
                                int scrollX = (codeIdx < it.CodeScroll.Count) ? it.CodeScroll[codeIdx] : 0;
                                int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                                Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                                if (thumb.Contains(virt))
                                {
                                    info.Hit = true; info.Which = CodeUiHit.ScrollThumb; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ScrollTrackRect = track; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                                }
                                if (track.Contains(virt))
                                {
                                    info.Hit = true; info.Which = CodeUiHit.ScrollTrack; info.Item = it; info.Block = blk; info.CodeIndex = codeIdx; info.ScrollTrackRect = track; info.ContentWidth = content.Width; info.ViewportWidth = textRect.Width; return info;
                                }
                            }

                            y += box.Height + 4;
                            codeIdx++;
                        }
                        else if (blk.Type == BlockType.Table)
                        {
                            var t = (TableBlock)blk;
                            int cols = Math.Max(0, t.Alignments != null ? t.Alignments.Count : 0);
                            if (cols > 0)
                            {
                                int cellPad = 6; int border = 1;
                                // measure intrinsic widths
                                int[] colWidths = new int[cols];
                                int headerH = 0;
                                for (int c = 0; c < cols; c++)
                                {
                                    var inl = (c < t.Header.Count) ? t.Header[c] : new List<InlineRun>();
                                    Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                    colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                    headerH = Math.Max(headerH, sz.Height);
                                }
                                int[] rowHeights = new int[t.Rows.Count];
                                for (int r = 0; r < t.Rows.Count; r++)
                                {
                                    int rowH = 0;
                                    for (int c = 0; c < cols; c++)
                                    {
                                        var inl = (c < t.Rows[r].Count) ? t.Rows[r][c] : new List<InlineRun>();
                                        Size sz = MeasureInlineParagraph(inl, _baseFont, int.MaxValue / 4, false);
                                        colWidths[c] = Math.Max(colWidths[c], sz.Width);
                                        rowH = Math.Max(rowH, sz.Height);
                                    }
                                    rowHeights[r] = rowH;
                                }
                                int intrinsicW = border; for (int c = 0; c < cols; c++) intrinsicW += colWidths[c] + cellPad * 2 + border;
                                int viewportW = Math.Max(0, contentW);
                                int tableH = 1 + headerH + cellPad * 2 + 1; for (int r = 0; r < rowHeights.Length; r++) tableH += rowHeights[r] + cellPad * 2 + 1;
                                bool needH = intrinsicW > viewportW;
                                if (needH)
                                {
                                    Rectangle track = new Rectangle(contentX, y + tableH + 2, Math.Min(contentW, intrinsicW), CodeHScrollHeight - 4);
                                    int tableIndex = 0; for (int bi = 0; bi < it.Blocks.Count && !object.ReferenceEquals(it.Blocks[bi], blk); bi++) if (it.Blocks[bi].Type == BlockType.Table) tableIndex++;
                                    if (it.TableScroll == null) it.TableScroll = new List<int>();
                                    while (it.TableScroll.Count <= tableIndex) it.TableScroll.Add(0);
                                    int scrollX = it.TableScroll[tableIndex];
                                    int maxScroll = Math.Max(0, intrinsicW - track.Width);
                                    int thumbW = Math.Max(CodeHScrollThumbMin, (int)Math.Round((double)track.Width * track.Width / Math.Max(1, intrinsicW)));
                                    int trackRange = Math.Max(1, track.Width - thumbW);
                                    int thumbX = track.X + (maxScroll > 0 ? (int)Math.Round((double)scrollX / maxScroll * trackRange) : 0);
                                    Rectangle thumb = new Rectangle(thumbX, track.Y, thumbW, track.Height);
                                    if (thumb.Contains(virt))
                                    { info.Hit = true; info.Which = CodeUiHit.ScrollThumb; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex; info.ScrollTrackRect = track; info.ContentWidth = intrinsicW; info.ViewportWidth = track.Width; info.IsTable = true; return info; }
                                    if (track.Contains(virt))
                                    { info.Hit = true; info.Which = CodeUiHit.ScrollTrack; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex; info.ScrollTrackRect = track; info.ContentWidth = intrinsicW; info.ViewportWidth = track.Width; info.IsTable = true; return info; }
                                }

                                // Also report content area as a generic Text hit for tables so Shift+Wheel can be used anywhere over the table
                                Rectangle tableRect = new Rectangle(contentX, y, Math.Min(contentW, intrinsicW), tableH);
                                if (tableRect.Contains(virt))
                                {
                                    int tableIndex2 = 0; for (int bi = 0; bi < it.Blocks.Count && !object.ReferenceEquals(it.Blocks[bi], blk); bi++) if (it.Blocks[bi].Type == BlockType.Table) tableIndex2++;
                                    info.Hit = true; info.Which = CodeUiHit.Text; info.Item = it; info.Block = blk; info.CodeIndex = -1; info.TableIndex = tableIndex2; info.ContentWidth = intrinsicW; info.ViewportWidth = Math.Min(contentW, intrinsicW); info.IsTable = true; return info;
                                }

                                y += tableH + (needH ? CodeHScrollHeight : 0) + 2;
                            }
                        }
                    }
                }
            }
            return info;
        }

        private MessageItem GetCurrentMessageFromY(int contentTopY)
        {
            // Helper: find message item whose content starts at contentTopY (approximate by bounds Y)
            foreach (var it in _items)
            {
                if (it.Bounds.Y + BubblePadding == contentTopY) return it;
            }
            return null;
        }
    }
}

