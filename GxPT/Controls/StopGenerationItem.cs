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
    // Owner-drawn ToolStripItem (like ContextMeterItem) styled to match the tab strip's new/close
    // glyph buttons (GlyphToolStripButton): a dark-grey filled-square glyph, a light-grey
    // hover/press fill with a matching border, and a thin resting border. The glyph is painted
    // (not a font character) so it stays a crisp square and never clips.
    internal sealed class StopGenerationItem : ToolStripItem
    {
        private bool _hover;
        private bool _pressed;

        public const int ItemSide = 16; // square; fits the 22px strip with 3px margins

        // Mirrors GlyphToolStripButton's palette so the two button families look identical.
        private static readonly Color GlyphColor = Color.FromArgb(80, 80, 80);
        private static readonly Color HoverBorder = Color.FromArgb(210, 210, 210);
        private static readonly Color RestBorder = Color.FromArgb(128, 128, 128); // thin dark-grey resting border

        public StopGenerationItem()
        {
            this.AutoSize = false;
            this.Size = new Size(ItemSide, ItemSide);
        }

        protected override Size DefaultSize
        {
            get { return new Size(ItemSide, ItemSide); }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Rectangle r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

            // Paint the full rect each pass (the strip's renderer doesn't clear behind plain items).
            using (SolidBrush bg = new SolidBrush(this.BackColor))
                g.FillRectangle(bg, 0, 0, this.Width, this.Height);

            // Hover/press fill + border, matching the tab glyph buttons.
            if (_hover || _pressed)
            {
                int shade = _pressed ? 210 : 230;
                using (SolidBrush sb = new SolidBrush(Color.FromArgb(shade, shade, shade)))
                    g.FillRectangle(sb, r);
                using (Pen hp = new Pen(HoverBorder))
                    g.DrawRectangle(hp, r);
            }
            else
            {
                // Thin dark-grey resting border.
                using (Pen rp = new Pen(RestBorder))
                    g.DrawRectangle(rp, r);
            }

            // Centered filled square (the "stop" glyph), in the tab buttons' glyph colour.
            int side = Math.Min(r.Width, r.Height) - 8;
            if (side < 6) side = 6;
            int x = r.Left + (r.Width - side) / 2;
            int y = r.Top + (r.Height - side) / 2;
            using (SolidBrush gb = new SolidBrush(GlyphColor))
                g.FillRectangle(gb, new Rectangle(x, y, side, side));
        }
    }
}
