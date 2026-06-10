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
    // DPI - a Unicode glyph in a stock Button clips vertically and reads as a rectangle. The button is
    // docked Right with its width pinned to the padded strip height, so it lays out as a real square.
    internal sealed class GenerationStatusBar : Panel
    {
        private readonly ProgressBar _bar;
        private readonly StopGlyphButton _stop;

        private const int StripHeight = 28;
        private const int VPad = 5; // top/bottom inset; also fixes the (square) button's side length

        // Raised on the UI thread when the user clicks Stop. The host cancels the in-flight request.
        public event EventHandler StopRequested;

        public GenerationStatusBar()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.Height = StripHeight;
            this.Padding = new Padding(8, VPad, 6, VPad);

            // Fill bar added before the docked button so it occupies the space to the button's left
            // (WinForms lays the Fill child into whatever the docked siblings leave).
            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Fill;
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;

            _stop = new StopGlyphButton();
            _stop.Dock = DockStyle.Right;
            // Docked Right gives the button the full padded height; pinning Width to that same value
            // makes it a square (StripHeight minus the top+bottom padding).
            _stop.Width = StripHeight - (VPad * 2);
            _stop.Margin = new Padding(2, 0, 0, 0); // 2px gap from the bar
            _stop.Click += delegate
            {
                EventHandler h = StopRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_stop, "Stop generating");

            this.Controls.Add(_bar);
            this.Controls.Add(_stop);
        }

        // Show the strip and start the marquee. Pulls current theme colors so it blends in dark mode.
        public void ShowBusy()
        {
            ApplyTheme();
            _bar.MarqueeAnimationSpeed = 30; // (re)start the animation if it had been stopped
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
            public Color BorderColor = Color.Gray;

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

                // Thin dark-grey border around the button.
                using (Pen bp = new Pen(BorderColor))
                using (GraphicsPath bdr = Rounded(new Rectangle(0, 0, Width - 1, Height - 1), 4))
                    g.DrawPath(bp, bdr);
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
