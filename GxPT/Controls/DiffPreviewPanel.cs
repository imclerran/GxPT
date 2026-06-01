// DiffPreviewPanel.cs
// Owner-drawn, auto-scrolling panel that renders a unified-diff body (via the "diff" syntax
// highlighter) with a path header. Used by the tool approval prompt to show a files__edit change
// instead of raw JSON. AutoScroll handles both tall and wide diffs.
// Target: .NET 3.5, Windows XP compatible.

using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    internal sealed class DiffPreviewPanel : Panel
    {
        private string _path = string.Empty;
        private string _body = string.Empty;
        private bool _dark;
        private Font _monoFont;
        private Color _codeBack = Color.FromArgb(245, 245, 245);
        private Color _foreColor = SystemColors.WindowText;

        private const int Pad = 6;

        public DiffPreviewPanel()
        {
            this.AutoScroll = true;
            this.BorderStyle = BorderStyle.None;
            this.BackColor = _codeBack;
            try { this.DoubleBuffered = true; }
            catch { }
        }

        // Set the diff to display. monoFont is owned by the caller; colors come from the active theme.
        public void SetDiff(string path, string body, bool dark, Font monoFont, Color codeBack, Color foreColor)
        {
            _path = path ?? string.Empty;
            _body = body ?? string.Empty;
            _dark = dark;
            _monoFont = monoFont;
            _codeBack = codeBack;
            _foreColor = foreColor;
            this.BackColor = codeBack;
            UpdateScrollSize();
            this.AutoScrollPosition = new Point(0, 0);
            Invalidate();
        }

        private int HeaderHeight
        {
            get { return (this.Font != null ? this.Font.Height : 14) + Pad; }
        }

        private void UpdateScrollSize()
        {
            if (_monoFont == null || string.IsNullOrEmpty(_body)) { this.AutoScrollMinSize = Size.Empty; return; }
            try
            {
                using (Graphics g = CreateGraphics())
                {
                    var colored = SyntaxHighlightingRenderer.GetColoredSegments(_body, "diff", _monoFont, _dark);
                    Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
                    int w = content.Width + 2 * Pad;
                    int h = HeaderHeight + Math.Max(_monoFont.Height, content.Height) + Pad;
                    this.AutoScrollMinSize = new Size(w, h);
                }
            }
            catch { this.AutoScrollMinSize = Size.Empty; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            using (var bg = new SolidBrush(_codeBack))
                g.FillRectangle(bg, this.ClientRectangle);
            if (_monoFont == null) return;

            // Draw everything in content coordinates; AutoScroll moves the world for both axes.
            g.TranslateTransform(this.AutoScrollPosition.X, this.AutoScrollPosition.Y);

            // Path header (the change is still pending here, so just the path — no tense)
            Font hf = this.Font ?? _monoFont;
            using (var br = new SolidBrush(_foreColor))
                g.DrawString(_path, hf, br, new PointF(Pad, Pad / 2f));

            if (string.IsNullOrEmpty(_body)) return;

            // Diff body
            SyntaxHighlightingRenderer.EnqueueHighlight("diff", _dark, _body, _monoFont);
            var colored = SyntaxHighlightingRenderer.GetColoredSegments(_body, "diff", _monoFont, _dark);
            Size content = SyntaxHighlightingRenderer.MeasureColoredSegmentsNoWrap(g, colored);
            int bodyH = Math.Max(_monoFont.Height, content.Height);
            int bodyW = Math.Max(content.Width, this.ClientSize.Width - 2 * Pad);
            Rectangle textRect = new Rectangle(Pad, HeaderHeight, bodyW, bodyH);
            SyntaxHighlightingRenderer.DrawColoredSegmentsNoWrap(g, colored, textRect, 0);
        }
    }
}
