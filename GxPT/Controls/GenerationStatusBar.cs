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
    // The stop button matches the tab strip's new/close glyph buttons (GlyphToolStripButton): an
    // owner-drawn dark-grey glyph with a light-grey hover/press fill, so it reads as part of the same
    // family. The glyph is painted (not a font character) so it stays a crisp square and never clips.
    internal sealed class GenerationStatusBar : Panel
    {
        private readonly ProgressBar _bar;
        private readonly Panel _stopHolder;
        private readonly StopGlyphButton _stop;

        private const int StripHeight = 28;
        private const int VPad = 5;      // top/bottom inset; also fixes the (square) button's side
        private const int BarGap = 2;    // gap between the progress bar and the stop button

        // Raised on the UI thread when the user clicks Stop. The host cancels the in-flight request.
        public event EventHandler StopRequested;

        public GenerationStatusBar()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.Height = StripHeight;
            this.Padding = new Padding(8, VPad, 6, VPad);

            int side = StripHeight - (VPad * 2); // square button side

            // Fill bar added before the docked holder so it occupies the space to the holder's left.
            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Fill;
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;

            // A docked Right-edge holder whose left padding provides a reliable gap between the bar and
            // the button (a docked control's own Margin is ignored by the layout, so the gap lives here).
            _stop = new StopGlyphButton();
            _stop.Dock = DockStyle.Fill;
            _stop.Click += delegate
            {
                EventHandler h = StopRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            _stopHolder = new Panel();
            _stopHolder.Dock = DockStyle.Right;
            _stopHolder.Width = side + BarGap;
            _stopHolder.Padding = new Padding(BarGap, 0, 0, 0);
            _stopHolder.Controls.Add(_stop);

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_stop, "Stop generating");

            this.Controls.Add(_bar);
            this.Controls.Add(_stopHolder);
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
                _stopHolder.BackColor = tc.UiBackground;
                _stop.BackColor = tc.UiBackground;
            }
            catch { }
        }

        // A flat, square stop button styled to match the tab strip's new/close buttons
        // (GlyphToolStripButton): a dark-grey filled-square glyph, a light-grey hover/press fill with a
        // matching border, and a thin resting border. Owner-drawn so the glyph never clips. Control
        // already raises Click on mouse click.
        private sealed class StopGlyphButton : Control
        {
            private bool _hover;
            private bool _pressed;

            // Mirrors GlyphToolStripButton's palette so the two button families look identical.
            private static readonly Color GlyphColor = Color.FromArgb(80, 80, 80);
            private static readonly Color HoverBorder = Color.FromArgb(210, 210, 210);
            private static readonly Color RestBorder = Color.FromArgb(128, 128, 128); // thin dark-grey resting border

            public StopGlyphButton()
            {
                this.SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                    | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                this.Cursor = Cursors.Hand;
                this.TabStop = false;
            }

            protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); } base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.Clear(this.BackColor);
                Rectangle r = new Rectangle(0, 0, this.Width - 1, this.Height - 1);

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
}
