using System;
using System.Drawing;
using System.Windows.Forms;

namespace GxPT
{
    internal sealed class InputManager
    {
        private const int MinInputHeightPx = 75;

        private readonly MainForm _mainForm;
        private readonly TextBox _txtMessage;
        private readonly Panel _pnlInput;
        private readonly Button _btnSend;
        private readonly ComboBox _cmbModel;
        private readonly SplitContainer _splitContainer;
        private readonly Panel _pnlApiKeyBanner;

        public InputManager(MainForm mainForm, TextBox txtMessage, Panel pnlInput,
            Button btnSend, ComboBox cmbModel, SplitContainer splitContainer,
            Panel pnlApiKeyBanner)
        {
            _mainForm = mainForm;
            _txtMessage = txtMessage;
            _pnlInput = pnlInput;
            _btnSend = btnSend;
            _cmbModel = cmbModel;
            _splitContainer = splitContainer;
            _pnlApiKeyBanner = pnlApiKeyBanner;

            InitializeInput();
            WireEvents();
        }

        private void InitializeInput()
        {
            if (_txtMessage != null)
            {
                _txtMessage.Multiline = true;
                _txtMessage.AcceptsReturn = true;
                _txtMessage.WordWrap = true;
                _txtMessage.ScrollBars = ScrollBars.None;
            }

            try
            {
                if (_pnlInput != null)
                    _pnlInput.AutoSize = false;
            }
            catch { }

            try { AdjustInputBoxHeight(); }
            catch { }
        }

        private void WireEvents()
        {
            if (_txtMessage != null)
            {
                _txtMessage.KeyDown += txtMessage_KeyDown;
                _txtMessage.TextChanged += (s, e) => AdjustInputBoxHeight();
                _txtMessage.Resize += (s, e) => AdjustInputBoxHeight();
                //try { _txtMessage.Resize += (s, e) => AdjustInputBoxHeight(); }
                //catch { }
            }

            // Recalculate input height when containers resize
            try
            {
                if (_splitContainer != null && _splitContainer.Panel2 != null)
                    _splitContainer.Panel2.Resize += (s, e) => AdjustInputBoxHeight();
            }
            catch { }

            // And when the API key banner changes visibility/size
            try
            {
                if (_pnlApiKeyBanner != null)
                {
                    _pnlApiKeyBanner.VisibleChanged += (s, e) => AdjustInputBoxHeight();
                    _pnlApiKeyBanner.Resize += (s, e) => AdjustInputBoxHeight();
                }
            }
            catch { }
        }

        private void txtMessage_KeyDown(object sender, KeyEventArgs e)
        {
            // Ctrl+A selects all text in the input box
            if (e.Control && e.KeyCode == Keys.A)
            {
                try { if (_txtMessage != null) _txtMessage.SelectAll(); }
                catch { }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                // Prevent newline and trigger send
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (_btnSend != null)
                    _btnSend.PerformClick();
            }
        }

        public void ResetInputBoxHeight()
        {
            if (_pnlInput != null)
            {
                try { _pnlInput.Height = MinInputHeightPx; }
                catch { }
            }
            if (_txtMessage != null)
            {
                _txtMessage.ScrollBars = ScrollBars.None;
            }
        }

        public void AdjustInputBoxHeight()
        {
            if (_txtMessage == null || _pnlInput == null) return;

            // Available area: the right panel that hosts the transcript + input, minus any banner
            int available = GetAvailableChatAreaHeight();
            int maxHeight = Math.Max(MinInputHeightPx, (int)Math.Floor(available * 0.5));

            // Measure required height for current text within the textbox width (wrap-aware)
            int width = Math.Max(10, _txtMessage.ClientSize.Width);
            var proposed = new Size(width, int.MaxValue);
            var flags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;
            string text = _txtMessage.Text;
            if (string.IsNullOrEmpty(text)) text = " ";
            Size measured = TextRenderer.MeasureText(text, _txtMessage.Font, proposed, flags);

            // Add a small padding to avoid clipping last line
            int desired = measured.Height + 8;

            // Ensure the input panel is never shorter than the right column (Send + Model)
            // Use PreferredSize to respect runtime font/DPI scaling and current theme.
            int rightMin = 0;
            try
            {
                if (_btnSend != null && _btnSend.Visible)
                    rightMin += _btnSend.PreferredSize.Height;
            }
            catch { }
            try
            {
                if (_cmbModel != null && _cmbModel.Visible)
                    rightMin += _cmbModel.PreferredSize.Height;
            }
            catch { }
            // Small padding between stacked controls to avoid visual crowding
            rightMin += 4;

            // Allow exceeding the original maxHeight when the right column needs more space
            int heightCap = Math.Max(maxHeight, rightMin);

            // Clamp to [MinInputHeightPx, maxHeight]
            int newHeight = Math.Max(MinInputHeightPx, Math.Min(desired, heightCap));

            // Apply height to the container panel (textbox is Dock:Fill)
            if (_pnlInput.Height != newHeight)
                _pnlInput.Height = newHeight;

            // Manage scrollbars: only show when content exceeds cap
            bool exceedsCap = desired > heightCap;
            var newScroll = exceedsCap ? ScrollBars.Vertical : ScrollBars.None;
            if (_txtMessage.ScrollBars != newScroll)
                _txtMessage.ScrollBars = newScroll;
        }

        // Determine the vertical space shared by the transcript and input within the main chat container.
        private int GetAvailableChatAreaHeight()
        {
            try
            {
                Control container = (_splitContainer != null) ? (Control)_splitContainer.Panel2 : _mainForm;
                int h = container.ClientSize.Height;

                // Subtract the API key banner height when visible (banner is hosted in pnlBottom)
                if (_pnlApiKeyBanner != null && _pnlApiKeyBanner.Visible)
                    h = Math.Max(0, h - _pnlApiKeyBanner.Height);
                return Math.Max(0, h);
            }
            catch
            {
                var menuStrip = _mainForm.GetMenuStrip();
                int menuH = (menuStrip != null ? menuStrip.Height : 0);
                return Math.Max(0, _mainForm.ClientSize.Height - menuH);
            }
        }

        public void ClearInput()
        {
            try
            {
                if (_txtMessage != null)
                {
                    _txtMessage.Clear();
                    ResetInputBoxHeight();
                }
            }
            catch { }
        }

        public string GetInputText()
        {
            try
            {
                return (_txtMessage != null ? _txtMessage.Text : string.Empty).Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void FocusInput()
        {
            try
            {
                if (_txtMessage == null) return;
                if (!_txtMessage.CanFocus) return;
                _txtMessage.Focus();
                // place caret at end
                try
                {
                    _txtMessage.SelectionStart = _txtMessage.TextLength;
                    _txtMessage.SelectionLength = 0;
                }
                catch { }
            }
            catch { }
        }

        public void FocusInputSoon()
        {
            try
            {
                _mainForm.BeginInvoke((MethodInvoker)(() => FocusInput()));
            }
            catch { }
        }

        public void HandleKeyDown(KeyEventArgs e)
        {
            // Ctrl+A selects all text in the input box
            if (e.Control && e.KeyCode == Keys.A)
            {
                try { if (_txtMessage != null) _txtMessage.SelectAll(); }
                catch { }
                e.SuppressKeyPress = true;
                e.Handled = true;
                return;
            }

            if (e.KeyCode == Keys.Enter && !e.Shift)
            {
                // Prevent newline and trigger send
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (_btnSend != null) _btnSend.PerformClick();
            }
        }

        public void SetHintText(string hintText) {
            if(string.IsNullOrEmpty(_txtMessage.Text))
            {
                _txtMessage.Text = hintText;
                _txtMessage.ForeColor = System.Drawing.Color.Gray;
            }
        }

        public void RemoveHintText(string hintText)
        {
            if (_txtMessage.Text == hintText)
            {
                _txtMessage.Text = "";
            }
        }
    }
}
