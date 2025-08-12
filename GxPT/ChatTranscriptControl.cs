//using System;
//using System.Collections.Generic;
//using System.ComponentModel;
//using System.Drawing;
//using System.Data;
//using System.Linq;
//using System.Text;
//using System.Windows.Forms;

//namespace GxPT
//{
//    public partial class ChatTranscriptControl : UserControl
//    {
//        public ChatTranscriptControl()
//        {
//            InitializeComponent();
//        }
//    }
//}

// ChatTranscriptControl_Markdown.cs
// WinForms owner-drawn chat transcript with basic Markdown rendering
// Target: .NET 3.5, Windows XP compatible (no external deps)

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace XpChat
{
    public enum MessageRole { User, Assistant, System }

    [ToolboxItem(true)]
    public sealed partial class ChatTranscriptControl : UserControl
    {
        // ---------- Layout constants (tweak to taste) ----------
        private const int MarginOuter = 8;
        private const int GapBetweenBubbles = 6;
        private const int BubblePadding = 8;
        private const int BubbleRadius = 8;
        private const int ScrollStep = 40;
        private const int MaxBubbleWidth = 560; // cap to keep lines readable
        private const int BulletIndent = 18;
        private const int BulletGap = 8;
        private const int CodeBlockPadding = 6;
        private const int InlineCodePaddingX = 3;
        private const int InlineCodePaddingY = 1;

        // Colors (XP-friendly)
        private static readonly Color UserBack = Color.FromArgb(225, 240, 255);
        private static readonly Color UserBorder = Color.FromArgb(160, 190, 220);

        private static readonly Color AsstBack = Color.FromArgb(235, 235, 235);
        private static readonly Color AsstBorder = Color.FromArgb(200, 200, 200);

        private static readonly Color SysBack = Color.FromArgb(255, 250, 220);
        private static readonly Color SysBorder = Color.FromArgb(210, 200, 150);

        private static readonly Color CodeBlockBack = Color.FromArgb(245, 245, 245);
        private static readonly Color CodeBlockBorder = Color.FromArgb(210, 210, 210);
        private static readonly Color InlineCodeBack = Color.FromArgb(240, 240, 240);
        private static readonly Color InlineCodeBorder = Color.FromArgb(200, 200, 200);

        // ---------- Fonts ----------
        private Font _baseFont;         // default UI font
        private Font _boldFont;
        private Font _italicFont;
        private Font _boldItalicFont;
        private Font _monoFont;         // code spans
        private Font _h1, _h2, _h3, _h4, _h5, _h6;

        private readonly VScrollBar _vbar;
        private int _contentHeight;
        private int _scrollOffset;

        private readonly ContextMenu _ctx;
        private MessageItem _ctxHit;

        // ---------- Data ----------
        private sealed class MessageItem
        {
            public MessageRole Role;
            public string RawMarkdown;
            public Rectangle Bounds; // bubble bounds, virtual coords
            public int MeasuredHeight;
            public List<Block> Blocks; // parsed markdown
        }

        private readonly List<MessageItem> _items = new List<MessageItem>();

        // ---------- Markdown model ----------
        private enum BlockType { Paragraph, Heading, CodeBlock, BulletList }
        private enum InlineStyle { Normal = 0, Bold = 1, Italic = 2, Code = 4 }

        [Flags]
        private enum RunStyle { Normal = 0, Bold = 1, Italic = 2, Code = 4 }

        private abstract class Block
        {
            public BlockType Type;
        }

        private sealed class HeadingBlock : Block
        {
            public int Level;
            public List<InlineRun> Inlines;
        }

        private sealed class ParagraphBlock : Block
        {
            public List<InlineRun> Inlines;
        }

        private sealed class CodeBlock : Block
        {
            public string Text;
        }

        private sealed class BulletListBlock : Block
        {
            public List<List<InlineRun>> Items = new List<List<InlineRun>>();
        }

        private sealed class InlineRun
        {
            public string Text;
            public RunStyle Style;
        }

        // ---------- ctor ----------
        public ChatTranscriptControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;

            _vbar = new VScrollBar();
            _vbar.Dock = DockStyle.Right;
            _vbar.Visible = true;
            _vbar.ValueChanged += delegate { _scrollOffset = _vbar.Value; Invalidate(); };
            Controls.Add(_vbar);

            _ctx = new ContextMenu(new[]
            {
                new MenuItem("Copy", delegate { if (_ctxHit != null) SafeClipboardSetText(_ctxHit.RawMarkdown ?? ""); })
            });

            _baseFont = this.Font;
            BuildFonts();

            this.AccessibleName = "Chat transcript";
            this.TabStop = true;
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
            var item = new MessageItem
            {
                Role = role,
                RawMarkdown = markdown,
                Blocks = ParseMarkdown(markdown)
            };
            _items.Add(item);
            Reflow();
            ScrollToBottom();
            Invalidate();
        }

        public void ClearMessages()
        {
            _items.Clear();
            _contentHeight = 0;
            _scrollOffset = 0;
            UpdateScrollbar();
            Invalidate();
        }

        // ---------- Markdown parsing ----------
        private static bool IsBullet(string line, out string content)
        {
            content = null;
            if (line.Length >= 2)
            {
                char c = line[0];
                if ((c == '-' || c == '*' || c == '+') && line.Length > 1 && (line[1] == ' ' || line[1] == '\t'))
                {
                    content = line.Substring(2).TrimEnd();
                    return true;
                }
            }
            return false;
        }

        private static int HeadingLevel(string line)
        {
            int i = 0;
            while (i < line.Length && i < 6 && line[i] == '#') i++;
            if (i > 0 && i < line.Length && line[i] == ' ') return i;
            return 0;
        }

        private static List<Block> ParseMarkdown(string md)
        {
            var blocks = new List<Block>();
            if (string.IsNullOrEmpty(md)) { blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() }); return blocks; }

            var lines = md.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inCode = false;
            var codeAccum = new System.Text.StringBuilder();
            var paraAccum = new System.Text.StringBuilder();
            List<string> bulletAccum = null;

            Action flushParagraph = delegate
            {
                string t = paraAccum.ToString().Trim();
                paraAccum.Length = 0;
                if (t.Length > 0)
                    blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = ParseInlines(t) });
            };

            Action flushBullets = delegate
            {
                if (bulletAccum != null && bulletAccum.Count > 0)
                {
                    var bl = new BulletListBlock { Type = BlockType.BulletList };
                    foreach (var li in bulletAccum)
                        bl.Items.Add(ParseInlines(li));
                    blocks.Add(bl);
                    bulletAccum = null;
                }
            };

            Action flushCode = delegate
            {
                if (codeAccum.Length > 0 || inCode)
                {
                    var text = codeAccum.ToString();
                    // Trim one leading/trailing newline if present
                    if (text.StartsWith("\n")) text = text.Substring(1);
                    if (text.EndsWith("\n")) text = text.Substring(0, text.Length - 1);
                    blocks.Add(new CodeBlock { Type = BlockType.CodeBlock, Text = text });
                    codeAccum.Length = 0;
                }
            };

            foreach (string raw in lines)
            {
                string line = raw;
                if (line.StartsWith("```"))
                {
                    if (!inCode)
                    {
                        // enter code: first flush any open containers
                        flushParagraph();
                        flushBullets();
                        inCode = true;
                    }
                    else
                    {
                        // leave code
                        inCode = false;
                        flushCode();
                    }
                    continue;
                }

                if (inCode)
                {
                    codeAccum.Append(line).Append('\n');
                    continue;
                }

                // blank line → flush paragraph/bullets
                if (line.Trim().Length == 0)
                {
                    flushParagraph();
                    flushBullets();
                    continue;
                }

                // heading?
                int h = HeadingLevel(line);
                if (h > 0)
                {
                    flushParagraph();
                    flushBullets();
                    string content = line.Substring(h + 1).TrimEnd();
                    blocks.Add(new HeadingBlock { Type = BlockType.Heading, Level = h, Inlines = ParseInlines(content) });
                    continue;
                }

                // bullet?
                string bulletText;
                if (IsBullet(line, out bulletText))
                {
                    flushParagraph();
                    if (bulletAccum == null) bulletAccum = new List<string>();
                    bulletAccum.Add(bulletText);
                    continue;
                }

                // otherwise paragraph text (accumulate)
                if (paraAccum.Length > 0) paraAccum.Append(' ');
                paraAccum.Append(line.Trim());
            }

            // end flush
            if (inCode) { inCode = false; }
            flushCode();
            flushParagraph();
            flushBullets();

            if (blocks.Count == 0)
                blocks.Add(new ParagraphBlock { Type = BlockType.Paragraph, Inlines = new List<InlineRun>() });

            return blocks;
        }

        // Very simple inline parser: **bold**, *italic*, __bold__, _italic_, `code`
        // No nested emphasis beyond bold+italic combos; escapes not handled exhaustively (sufficient for chat)
        private static List<InlineRun> ParseInlines(string text)
        {
            var runs = new List<InlineRun>();
            if (string.IsNullOrEmpty(text)) { runs.Add(new InlineRun { Text = "", Style = RunStyle.Normal }); return runs; }

            int i = 0;
            RunStyle style = RunStyle.Normal;
            var sb = new System.Text.StringBuilder();
            Func<RunStyle, FontStyle> _ = s => FontStyle.Regular;

            Action flush = delegate
            {
                if (sb.Length > 0)
                {
                    runs.Add(new InlineRun { Text = sb.ToString(), Style = style });
                    sb.Length = 0;
                }
            };

            while (i < text.Length)
            {
                // inline code
                if (text[i] == '`')
                {
                    flush();
                    i++;
                    int start = i;
                    while (i < text.Length && text[i] != '`') i++;
                    string code = text.Substring(start, i - start);
                    runs.Add(new InlineRun { Text = code, Style = RunStyle.Code });
                    if (i < text.Length && text[i] == '`') i++;
                    continue;
                }

                // **bold** or __bold__
                if (i + 1 < text.Length && ((text[i] == '*' && text[i + 1] == '*') || (text[i] == '_' && text[i + 1] == '_')))
                {
                    // toggle bold
                    bool turnOn = (style & RunStyle.Bold) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Bold; else style &= ~RunStyle.Bold;
                    i += 2;
                    continue;
                }

                // *italic* or _italic_
                if (text[i] == '*' || text[i] == '_')
                {
                    bool turnOn = (style & RunStyle.Italic) == 0;
                    flush();
                    if (turnOn) style |= RunStyle.Italic; else style &= ~RunStyle.Italic;
                    i++;
                    continue;
                }

                sb.Append(text[i]);
                i++;
            }
            flush();
            return runs;
        }

        // ---------- Layout ----------
        private void Reflow()
        {
            using (Graphics g = CreateGraphics())
            {
                int y = MarginOuter;
                int innerWidth = Math.Max(0, ClientSize.Width - _vbar.Width - 2 * MarginOuter);
                int usableWidth = Math.Min(innerWidth, MaxBubbleWidth);

                foreach (var it in _items)
                {
                    Size bubbleSize = MeasureBubble(it, usableWidth);
                    int xLeft = (it.Role == MessageRole.User)
                        ? MarginOuter + innerWidth - bubbleSize.Width
                        : MarginOuter;

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

            foreach (var blk in it.Blocks)
            {
                Size sz = MeasureBlock(blk, textMax);
                h += sz.Height;
                h += 4; // inter-block gap
                wUsed = Math.Max(wUsed, sz.Width);
            }

            h += BubblePadding; // bottom padding
            int bubbleW = Math.Min(maxBubbleWidth, wUsed + 2 * BubblePadding);
            return new Size(bubbleW, Math.Max(24, h));
        }

        private Size MeasureBlock(Block blk, int maxWidth)
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
                            int bulletWidth = BulletIndent;
                            Size sz = MeasureInlineParagraph(item, _baseFont, maxWidth - bulletWidth, true);
                            y += Math.Max(sz.Height, _baseFont.Height);
                            y += 2;
                            w = Math.Max(w, bulletWidth + sz.Width);
                        }
                        return new Size(Math.Min(maxWidth, w), y);
                    }
                case BlockType.CodeBlock:
                    {
                        var c = (CodeBlock)blk;
                        // Use TextRenderer with WordBreak + TextBoxControl to wrap code block
                        Size proposed = new Size(maxWidth - 2 * CodeBlockPadding, int.MaxValue);
                        Size text = TextRenderer.MeasureText(
                            c.Text.Length == 0 ? " " : c.Text,
                            _monoFont,
                            proposed,
                            TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);

                        int width = Math.Min(maxWidth, text.Width + 2 * CodeBlockPadding);
                        int height = Math.Max(_monoFont.Height + 2 * CodeBlockPadding, text.Height + 2 * CodeBlockPadding);
                        return new Size(width, height);
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
                    y += lineHeight;
                    x = 0;
                    maxLineWidth = Math.Max(maxLineWidth, seg.LineWidth);
                    lineHeight = baseFont.Height;
                    continue;
                }

                // track tallest on the current line
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }

            // add last line
            y += lineHeight;
            maxLineWidth = Math.Max(maxLineWidth, x);

            if (addBottomGap) y += 2;
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
        }

        private Font GetRunFont(RunStyle st, Font baseFont)
        {
            bool b = (st & RunStyle.Bold) != 0;
            bool i = (st & RunStyle.Italic) != 0;
            if (b && i) return _boldItalicFont;
            if (b) return _boldFont;
            if (i) return _italicFont;
            return baseFont;
        }

        private IEnumerable<LayoutSeg> WordWrapRuns(List<InlineRun> runs, Font baseFont, int maxWidth)
        {
            // Greedy word wrapping across style runs.
            int x = 0;
            int lineWidth = 0;
            int lineHeight = baseFont.Height;

            foreach (var r in runs)
            {
                bool isCode = (r.Style & RunStyle.Code) != 0;
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

                    Size sz = TextRenderer.MeasureText(text.Length == 0 ? " " : text, f, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPrefix);

                    int partWidth = sz.Width;
                    int partHeight = f.Height;

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
                        IsInlineCode = isCode
                    };

                    x += partWidth;
                    lineWidth += partWidth;
                    lineHeight = Math.Max(lineHeight, partHeight);
                }
            }
        }

        private static List<string> SplitWordsPreserveSpaces(string s)
        {
            var parts = new List<string>();
            if (string.IsNullOrEmpty(s)) { parts.Add(""); return parts; }

            int i = 0;
            var sb = new System.Text.StringBuilder();
            while (i < s.Length)
            {
                char c = s[i];
                if (c == ' ')
                {
                    // flush word
                    if (sb.Length > 0) { parts.Add(sb.ToString()); sb.Length = 0; }
                    // accumulate consecutive spaces as one
                    int j = i;
                    while (j < s.Length && s[j] == ' ') j++;
                    parts.Add(s.Substring(i, j - i));
                    i = j;
                }
                else
                {
                    sb.Append(c);
                    i++;
                }
            }
            if (sb.Length > 0) parts.Add(sb.ToString());
            return parts;
        }

        // ---------- Painting ----------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Reflow();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);

            Rectangle clip = e.ClipRectangle;
            e.Graphics.TranslateTransform(0, -_scrollOffset);

            foreach (var it in _items)
            {
                Rectangle r = it.Bounds;
                if (r.Bottom < _scrollOffset - GapBetweenBubbles) continue;
                if (r.Top > _scrollOffset + ClientSize.Height) break;

                DrawBubble(e.Graphics, it);
            }

            e.Graphics.ResetTransform();
            base.OnPaint(e);
        }

        private void DrawBubble(Graphics g, MessageItem it)
        {
            Rectangle r = it.Bounds;

            Color back, border;
            if (it.Role == MessageRole.User) { back = UserBack; border = UserBorder; }
            else if (it.Role == MessageRole.Assistant) { back = AsstBack; border = AsstBorder; }
            else { back = SysBack; border = SysBorder; }

            using (var path = RoundedRect(r, BubbleRadius))
            using (var b = new SolidBrush(back))
            using (var pen = new Pen(border))
            {
                g.FillPath(b, path);
                g.DrawPath(pen, path);
            }

            // Content area
            Rectangle content = new Rectangle(r.X + BubblePadding, r.Y + BubblePadding, r.Width - 2 * BubblePadding, r.Height - 2 * BubblePadding);
            DrawBlocks(g, content, it.Blocks);
        }

        private void DrawBlocks(Graphics g, Rectangle bounds, List<Block> blocks)
        {
            int y = bounds.Y;
            int x0 = bounds.X;
            int maxWidth = bounds.Width;

            foreach (var blk in blocks)
            {
                if (blk.Type == BlockType.Heading)
                {
                    var h = (HeadingBlock)blk;
                    Font f = GetHeadingFont(h.Level);
                    y += DrawInlineParagraph(g, x0, y, maxWidth, h.Inlines, f);
                    y += 4;
                }
                else if (blk.Type == BlockType.Paragraph)
                {
                    var p = (ParagraphBlock)blk;
                    y += DrawInlineParagraph(g, x0, y, maxWidth, p.Inlines, _baseFont);
                    y += 2;
                }
                else if (blk.Type == BlockType.BulletList)
                {
                    var list = (BulletListBlock)blk;
                    foreach (var item in list.Items)
                    {
                        // bullet glyph
                        int bulletYTop = y + (_baseFont.Height - _baseFont.Height) / 2;
                        using (var b = new SolidBrush(ForeColor))
                        {
                            // simple bullet
                            g.FillEllipse(b, x0, y + _baseFont.Height / 2 - 2, 4, 4);
                        }
                        int textX = x0 + BulletIndent;
                        int used = DrawInlineParagraph(g, textX, y, maxWidth - BulletIndent, item, _baseFont);
                        y += Math.Max(used, _baseFont.Height) + 2;
                    }
                }
                else if (blk.Type == BlockType.CodeBlock)
                {
                    var c = (CodeBlock)blk;
                    Size proposed = new Size(maxWidth - 2 * CodeBlockPadding, int.MaxValue);
                    Size text = TextRenderer.MeasureText(
                        c.Text.Length == 0 ? " " : c.Text,
                        _monoFont,
                        proposed,
                        TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);

                    Rectangle box = new Rectangle(x0, y, Math.Min(maxWidth, text.Width + 2 * CodeBlockPadding), Math.Max(_monoFont.Height, text.Height) + 2 * CodeBlockPadding);

                    using (var sb = new SolidBrush(CodeBlockBack))
                    using (var pen = new Pen(CodeBlockBorder))
                    {
                        g.FillRectangle(sb, box);
                        g.DrawRectangle(pen, box);
                    }

                    Rectangle textRect = new Rectangle(box.X + CodeBlockPadding, box.Y + CodeBlockPadding, box.Width - 2 * CodeBlockPadding, box.Height - 2 * CodeBlockPadding);
                    TextRenderer.DrawText(g, c.Text.Length == 0 ? " " : c.Text, _monoFont, textRect,
                        ForeColor, TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.WordBreak);

                    y += box.Height + 4;
                }
            }
        }

        private int DrawInlineParagraph(Graphics g, int x, int y, int maxWidth, List<InlineRun> runs, Font baseFont)
        {
            int xCursor = x;
            int yCursor = y;
            int lineHeight = baseFont.Height;
            int lineWidth = 0;

            foreach (var seg in WordWrapRuns(runs, baseFont, maxWidth))
            {
                if (seg.IsNewLine)
                {
                    yCursor += lineHeight;
                    xCursor = x;
                    lineWidth = 0;
                    lineHeight = baseFont.Height;
                    continue;
                }

                Rectangle r = new Rectangle(xCursor, yCursor, seg.Rect.Width, seg.Rect.Height);

                if (seg.IsInlineCode)
                {
                    Rectangle bg = new Rectangle(r.X - InlineCodePaddingX, r.Y - InlineCodePaddingY, r.Width + 2 * InlineCodePaddingX, r.Height + 2 * InlineCodePaddingY);
                    using (var sb = new SolidBrush(InlineCodeBack))
                    using (var pen = new Pen(InlineCodeBorder))
                    {
                        g.FillRectangle(sb, bg);
                        g.DrawRectangle(pen, bg);
                    }
                }

                TextRenderer.DrawText(g, seg.Text, seg.Font, r, ForeColor, TextFormatFlags.NoPrefix);
                xCursor += r.Width;
                lineWidth += r.Width;
                lineHeight = Math.Max(lineHeight, seg.Font.Height);
            }

            // last line height
            yCursor += lineHeight;
            return yCursor - y;
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
            if (!_vbar.Enabled) return;

            int delta = -(e.Delta / 120) * ScrollStep;
            int view = Math.Max(0, ClientSize.Height);
            int max = Math.Max(0, _contentHeight - view);
            _scrollOffset = Math.Max(0, Math.Min(max, _scrollOffset + delta));
            _vbar.Value = _scrollOffset;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right)
            {
                _ctxHit = HitTest(e.Location);
                if (_ctxHit != null)
                    _ctx.Show(this, e.Location);
            }
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

        private void ScrollToBottom()
        {
            int view = Math.Max(0, ClientSize.Height);
            int max = Math.Max(0, _contentHeight - view);
            _scrollOffset = max;
            if (_vbar.Enabled)
                _vbar.Value = Math.Max(_vbar.Minimum, Math.Min(_vbar.Maximum - _vbar.LargeChange + 1, max));
        }

        private void UpdateScrollbar()
        {
            int view = Math.Max(0, ClientSize.Height);
            int max = Math.Max(0, _contentHeight - view);
            if (max <= 0)
            {
                _vbar.Enabled = false;
                _vbar.Minimum = 0;
                _vbar.Maximum = 0;
                _vbar.Value = 0;
                _scrollOffset = 0;
            }
            else
            {
                _vbar.Enabled = true;
                _vbar.Minimum = 0;
                _vbar.LargeChange = Math.Max(1, view);
                _vbar.SmallChange = ScrollStep;
                _vbar.Maximum = max + _vbar.LargeChange - 1;
                _vbar.Value = Math.Max(0, Math.Min(max, _scrollOffset));
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
            try { Clipboard.SetText(s ?? ""); }
            catch { /* clipboard busy; ignore */ }
        }
    }
}

