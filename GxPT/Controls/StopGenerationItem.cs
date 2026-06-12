using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // The status bar's stop button, shown (next to the marquee progress bar) only while the active
    // tab has a model request in flight. Clicking raises Click, which the host wires to the active
    // tab's RequestCancellation so the in-flight curl process is killed (dropping the connection);
    // the streaming/orchestrator paths then finalize the turn cleanly and the indicator is hidden.
    //
    // Owner-drawn ToolStripItem (like ContextMeterItem) styled after the transcript's Retry button:
    // the theme's code-block fill with a 1px code border, the copy-button hover/pressed fills, and
    // a "Stop" label — drawn in the system control foreground rather than the Retry button's link
    // color, since the strip is system chrome. Height matches tspGenProgress exactly so the pair
    // reads as one row.
    internal sealed class StopGenerationItem : ToolStripItem
    {
        private bool _hover;
        private bool _pressed;

        private const int PadX = 8;        // horizontal label padding, matching RetryBtnPadX
        public const int ItemHeight = 15;  // tspGenProgress's height

        public StopGenerationItem()
        {
            this.AutoSize = false;
            this.Text = "Stop";
            this.Size = new Size(PreferredWidth(), ItemHeight);
        }

        protected override Size DefaultSize
        {
            get { return new Size(PreferredWidth(), ItemHeight); }
        }

        private int PreferredWidth()
        {
            try { return TextRenderer.MeasureText(this.Text, this.Font).Width + 2 * PadX; }
            catch { return 44; }
        }

        // The strip's font can change after construction (it is inherited); keep the width fitting
        // the label so "Stop" never clips at other font sizes/DPI.
        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            try { this.Width = PreferredWidth(); }
            catch { }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            // Resolve the same theme palette the transcript's Retry button paints with, so the two
            // buttons stay in step across light/dark theme switches.
            ThemeColors tc;
            try
            {
                string th = AppSettings.GetString("theme");
                bool dark = !string.IsNullOrEmpty(th) &&
                    th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                tc = ThemeService.GetColors(dark);
            }
            catch { tc = ThemeService.GetColors(false); }

            Rectangle bounds = new Rectangle(0, 0, this.Width, this.Height);
            Rectangle border = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

            Color fill = _pressed ? tc.CopyPressed : (_hover ? tc.CopyHover : tc.CodeBack);
            using (SolidBrush sb = new SolidBrush(fill))
                g.FillRectangle(sb, bounds);
            using (Pen pen = new Pen(tc.CodeBorder))
                g.DrawRectangle(pen, border);

            TextRenderer.DrawText(g, this.Text, this.Font, bounds, SystemColors.ControlText,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
        }
    }
}
