using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace GxPT
{
    // A thin status strip docked at the bottom of a tab's chat area, shown only while a model request
    // is in flight. It carries an indeterminate (marquee) progress bar spanning the width, with a
    // square stop button at the right end. Stopping raises StopRequested, which the host wires to the
    // turn's RequestCancellation so the in-flight curl process is killed (dropping the connection);
    // the streaming/orchestrator paths then finalize the turn cleanly and the bar is hidden.
    //
    // One instance per tab (created alongside the transcript), mirroring ToolApprovalPanel: it
    // self-docks Bottom and starts hidden. The marquee animation needs visual styles (enabled
    // app-wide in Program.cs), available on Windows XP and later; without them it renders static.
    //
    // The stop glyph is owner-drawn (not a font character) so it stays a crisp, true square at any
    // DPI - a Unicode glyph in a stock Button clips vertically and reads as a rectangle.
    internal sealed class GenerationStatusBar : Panel
    {
        private readonly ProgressBar _bar;
        private readonly StopGlyphButton _stop;

        private const int BarVPad = 5;   // vertical inset for both children
        private const int SideHPad = 6;  // gap from the panel's left/right edges
        private const int Gap = 6;       // gap between the progress bar and the stop button

        // Raised on the UI thread when the user clicks Stop. The host cancels the in-flight request.
        public event EventHandler StopRequested;

        public GenerationStatusBar()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.Height = 28;

            // Children are positioned manually (Relayout) so the stop button can be a centered square
            // rather than a full-height docked rectangle.
            _bar = new ProgressBar();
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;

            _stop = new StopGlyphButton();
            _stop.Click += delegate
            {
                EventHandler h = StopRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_stop, "Stop generating");

            this.Controls.Add(_bar);
            this.Controls.Add(_stop);
            Relayout();
        }

        // Show the strip and start the marquee. Pulls current theme colors so it blends in dark mode.
        public void ShowBusy()
        {
            ApplyTheme();
            _bar.MarqueeAnimationSpeed = 30; // (re)start the animation if it had been stopped
            Relayout();
            this.Visible = true;
            // Keep this Bottom-docked strip behind the Fill transcript in z-order, so the transcript
            // shrinks above it rather than the strip overlaying the transcript's bottom edge.
            this.SendToBack();
        }

        // Hide the strip and stop the marquee (no point animating an invisible bar).
        public void HideBusy()
        {
            this.Visible = false;
            _bar.MarqueeAnimationSpeed = 0;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            Relayout();
        }

        // The stop button is a square sized to the strip's height; the progress bar fills the rest.
        private void Relayout()
        {
            int w = this.ClientSize.Width;
            int h = this.ClientSize.Height;
            if (w <= 0 || h <= 0) return;

            int side = h - (BarVPad * 2);
            if (side < 12) side = 12;
            int stopX = w - SideHPad - side;
            _stop.SetBounds(stopX, (h - side) / 2, side, side);

            int barLeft = SideHPad;
            int barWidth = stopX - Gap - barLeft;
            if (barWidth < 0) barWidth = 0;
            int barHeight = side; // align the bar's height with the button for a balanced row
            _bar.SetBounds(barLeft, (h - barHeight) / 2, barWidth, barHeight);
        }

        private void ApplyTheme()
        {
            try
            {
                string th = AppSettings.GetString("theme");
                bool dark = !string.IsNullOrEmpty(th) && th.Trim().Equals("dark", StringComparison.OrdinalIgnoreCase);
                ThemeColors tc = ThemeService.GetColors(dark);
                this.BackColor = tc.UiBackground;
                _stop.BackColor = tc.UiBackground;
                _stop.GlyphColor = tc.UiForeground;
                // Subtle hover wash: nudge the background toward the foreground.
                _stop.HoverBack = Blend(tc.UiBackground, tc.UiForeground, dark ? 0.22f : 0.12f);
            }
            catch { }
        }

        private static Color Blend(Color a, Color b, float t)
        {
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            int r = (int)(a.R + (b.R - a.R) * t);
            int g = (int)(a.G + (b.G - a.G) * t);
            int bl = (int)(a.B + (b.B - a.B) * t);
            return Color.FromArgb(r, g, bl);
        }

        // A flat, square stop button that paints a centered filled square (slightly rounded) instead
        // of relying on a font glyph - so it never clips and always reads as a square. Hover paints a
        // soft rounded background for feedback. Control already raises Click on mouse click.
        private sealed class StopGlyphButton : Control
        {
            private bool _hover;

            public Color GlyphColor = Color.Black;
            public Color HoverBack = Color.Gainsboro;

            public StopGlyphButton()
            {
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                this.Cursor = Cursors.Hand;
                this.TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(this.BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                if (_hover)
                {
                    using (SolidBrush hb = new SolidBrush(HoverBack))
                    using (GraphicsPath hp = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 4))
                        g.FillPath(hb, hp);
                }

                int side = (int)(Math.Min(Width, Height) * 0.5f);
                if (side < 6) side = 6;
                int x = (Width - side) / 2;
                int y = (Height - side) / 2;
                using (SolidBrush sb = new SolidBrush(GlyphColor))
                using (GraphicsPath sp = Rounded(new Rectangle(x, y, side, side), 2))
                    g.FillPath(sb, sp);
            }

            private static GraphicsPath Rounded(Rectangle r, int radius)
            {
                GraphicsPath p = new GraphicsPath();
                int d = radius * 2;
                if (d <= 0 || r.Width <= d || r.Height <= d)
                {
                    p.AddRectangle(r);
                    return p;
                }
                p.AddArc(r.X, r.Y, d, d, 180, 90);
                p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                p.CloseFigure();
                return p;
            }
        }
    }
}
