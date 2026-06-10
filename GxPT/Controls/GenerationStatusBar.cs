using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    // A thin status strip docked at the bottom of a tab's chat area, shown only while a model request
    // is in flight. It carries an indeterminate (marquee) progress bar spanning the width, with a
    // stop button at the right end. Stopping raises StopRequested, which the host wires to the turn's
    // RequestCancellation so the in-flight curl process is killed (dropping the connection); the
    // streaming/orchestrator paths then finalize the turn cleanly and the bar is hidden.
    //
    // One instance per tab (created alongside the transcript), mirroring ToolApprovalPanel: it
    // self-docks Bottom and starts hidden. The marquee animation needs visual styles (enabled
    // app-wide in Program.cs), which are available on Windows XP and later; without them it simply
    // renders static rather than animating.
    internal sealed class GenerationStatusBar : Panel
    {
        private readonly ProgressBar _bar;
        private readonly Button _stop;

        // Raised on the UI thread when the user clicks Stop. The host cancels the in-flight request.
        public event EventHandler StopRequested;

        public GenerationStatusBar()
        {
            this.Dock = DockStyle.Bottom;
            this.Visible = false;
            this.Height = 26;
            this.Padding = new Padding(6, 3, 6, 3);

            _bar = new ProgressBar();
            _bar.Dock = DockStyle.Fill;
            _bar.Style = ProgressBarStyle.Marquee;
            _bar.MarqueeAnimationSpeed = 30;

            _stop = new Button();
            _stop.Dock = DockStyle.Right;
            _stop.Width = 26;
            _stop.Text = "\u25A0"; // BLACK SQUARE - present in Tahoma (XP's default UI font)
            _stop.FlatStyle = FlatStyle.Flat;
            _stop.TabStop = false;
            _stop.Margin = new Padding(0);
            _stop.Click += delegate
            {
                EventHandler h = StopRequested;
                if (h != null) h(this, EventArgs.Empty);
            };

            ToolTip tip = new ToolTip();
            tip.SetToolTip(_stop, "Stop generating");

            // The Fill bar is added before the docked button so it occupies the space to its left
            // (WinForms lays the Fill child into whatever the docked siblings leave).
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
                _stop.ForeColor = tc.UiForeground;
                _stop.BackColor = tc.UiBackground;
                _stop.FlatAppearance.BorderColor = tc.UiForeground;
            }
            catch { }
        }
    }
}
