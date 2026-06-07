using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GxPT
{
    // Token-aware autocomplete for the input box. Built on a borderless, non-activating top-level form
    // hosting a ListBox (see PopupForm for why not a ToolStripDropDown): the caret stays in the text
    // box, so typing and key navigation keep working, and it opens upward above the input.
    //
    // Two modes, switched purely on the field text (commands only ever start at position 0):
    //   * name mode  -- "/<prefix>"      -> filter the command registry.
    //   * path mode  -- "/<cmd> <arg>"   -> when the resolved command takes a path argument, complete
    //                                        against one directory under the working folder.
    // Path completion never offers anything WorkspacePath would reject (absolute, drive, ".."), so what
    // is offered is always valid at the file server.
    internal sealed class SlashAutocompleteController
    {
        private const int MaxRows = 8;       // visible rows before scrolling
        private const int MaxPathResults = 50;

        private readonly MainForm _host;
        private readonly InputManager _input;
        private readonly TextBox _txt;

        // A borderless, non-activating top-level form hosts the list. We deliberately do NOT use a
        // ToolStripDropDown: while one is open it installs an application-wide modal keyboard filter
        // (for menu navigation) that swallows every keystroke before it reaches the text box, which
        // killed typing. A plain non-activating form installs no such filter, so the text box keeps
        // focus and receives all keys; we drive the list selection manually from its KeyDown.
        private readonly PopupForm _popup;
        private readonly ListBox _list;

        private readonly List<Item> _items = new List<Item>();
        private bool _ignoreNextChange;

        // One-directory cache so typing within the same folder filters in memory instead of re-scanning.
        private string _cacheKey;
        private List<Entry> _cacheEntries;

        public SlashAutocompleteController(MainForm host, InputManager input, TextBox txt)
        {
            _host = host;
            _input = input;
            _txt = txt;

            _list = new ListBox();
            _list.BorderStyle = BorderStyle.FixedSingle;
            _list.IntegralHeight = false;
            _list.SelectionMode = SelectionMode.One;
            _list.Dock = DockStyle.Fill;
            _list.MouseClick += List_MouseClick;

            _popup = new PopupForm();
            _popup.Controls.Add(_list);
            if (_host != null) _popup.Owner = _host; // stays above the main window, closes with it

            if (_txt != null)
            {
                _txt.TextChanged += delegate { OnTextChanged(); };
                // Tab (and, defensively, the nav/accept keys) are otherwise consumed by WinForms during
                // key pre-processing for focus navigation, so KeyDown never sees them. Marking them as
                // input keys while the popup is open routes them to our KeyDown handler instead.
                _txt.PreviewKeyDown += Txt_PreviewKeyDown;
                // Safe now that the popup never takes focus on show: this only fires on a genuine focus
                // change (the user clicked another control), which is exactly when we want to close.
                _txt.LostFocus += delegate { Hide(); };
            }
        }

        public bool IsOpen
        {
            get { return _popup != null && _popup.Visible; }
        }

        // Called from InputManager's key handlers before its own Enter/Esc logic. Returns true when the
        // keystroke was consumed by the popup.
        public bool HandleKeyDown(KeyEventArgs e)
        {
            if (!IsOpen) return false;

            switch (e.KeyCode)
            {
                case Keys.Down:
                    Move(1);
                    e.Handled = true; e.SuppressKeyPress = true;
                    return true;
                case Keys.Up:
                    Move(-1);
                    e.Handled = true; e.SuppressKeyPress = true;
                    return true;
                case Keys.Tab:
                case Keys.Enter:
                    // While the popup is open, both accept the highlighted item (it always has a
                    // selection). Sending happens on the next Enter, once the popup has closed.
                    AcceptSelected();
                    e.Handled = true; e.SuppressKeyPress = true;
                    return true;
                case Keys.Escape:
                    Hide();
                    e.Handled = true; e.SuppressKeyPress = true;
                    return true;
                default:
                    return false;
            }
        }

        private void OnTextChanged()
        {
            if (_ignoreNextChange) { _ignoreNextChange = false; return; }
            try { Reevaluate(); }
            catch { Hide(); }
        }

        private void Reevaluate()
        {
            if (_txt == null || _input == null) { Hide(); return; }
            if (_input.TextIsHint) { Hide(); return; }

            string text = _txt.Text ?? string.Empty;
            if (text.Length == 0 || text[0] != '/') { Hide(); return; }

            string body = text.Substring(1);
            int sp = IndexOfWhitespace(body);
            if (sp < 0)
            {
                ShowNameMode(body);
            }
            else
            {
                string name = body.Substring(0, sp);
                string arg = body.Substring(sp + 1);
                ShowPathMode(name, arg);
            }
        }

        // ---- name mode ----

        private void ShowNameMode(string prefix)
        {
            SlashCommandRegistry registry = _host != null ? _host.GetSlashRegistry() : null;
            if (registry == null) { Hide(); return; }
            ISlashCommandContext ctx = _host.GetSlashContext();

            IList<ISlashCommand> matches = registry.Match(prefix);
            _items.Clear();
            for (int i = 0; i < matches.Count; i++)
            {
                ISlashCommand cmd = matches[i];
                string display = "/" + cmd.Name;
                if (!string.IsNullOrEmpty(cmd.ArgumentHint)) display += " " + cmd.ArgumentHint;
                if (!string.IsNullOrEmpty(cmd.Description)) display += "  -  " + cmd.Description;

                string reason = SlashCommandGate.UnavailableReason(cmd, ctx);
                if (reason != null) display += "  (" + reason + ")";

                Item it = new Item();
                it.Display = display;
                it.Insert = "/" + cmd.Name + " ";
                it.IsDirectory = false; // accepting a name closes the popup
                _items.Add(it);
            }

            Populate();
        }

        // ---- path mode ----

        private void ShowPathMode(string commandName, string arg)
        {
            SlashCommandRegistry registry = _host != null ? _host.GetSlashRegistry() : null;
            if (registry == null) { Hide(); return; }

            ISlashCommand cmd;
            if (!registry.TryResolve(commandName, out cmd) || !cmd.TakesPathArgument) { Hide(); return; }

            // Never suggest anything the rule would reject.
            if (!WorkspacePath.IsValid(arg)) { Hide(); return; }

            ISlashCommandContext ctx = _host.GetSlashContext();
            string workdir = ctx != null ? ctx.WorkingDir : null;
            if (string.IsNullOrEmpty(workdir)) { Hide(); return; }

            // Split the argument into "already-typed directory prefix" + "partial leaf".
            int lastSep = LastSeparator(arg);
            string dirPrefix = lastSep >= 0 ? arg.Substring(0, lastSep + 1) : string.Empty; // keeps the sep
            string baseRel = lastSep >= 0 ? arg.Substring(0, lastSep) : string.Empty;
            string leaf = lastSep >= 0 ? arg.Substring(lastSep + 1) : arg;

            string baseDirAbs;
            try { baseDirAbs = baseRel.Length > 0 ? Path.Combine(workdir, baseRel) : workdir; }
            catch { Hide(); return; }

            List<Entry> entries = GetEntries(baseDirAbs);

            _items.Clear();
            for (int i = 0; i < entries.Count && _items.Count < MaxPathResults; i++)
            {
                Entry en = entries[i];
                if (leaf.Length > 0 &&
                    !en.Name.StartsWith(leaf, StringComparison.OrdinalIgnoreCase))
                    continue;

                Item it = new Item();
                it.Display = en.Name + (en.IsDir ? "/" : string.Empty);
                it.Insert = "/" + commandName + " " + dirPrefix + en.Name + (en.IsDir ? "/" : string.Empty);
                it.IsDirectory = en.IsDir; // accepting a directory keeps the popup open to drill in
                _items.Add(it);
            }

            Populate();
        }

        private List<Entry> GetEntries(string baseDirAbs)
        {
            string key = (baseDirAbs ?? string.Empty).ToLowerInvariant();
            if (string.Equals(key, _cacheKey, StringComparison.Ordinal) && _cacheEntries != null)
                return _cacheEntries;

            List<Entry> list = new List<Entry>();
            try
            {
                if (Directory.Exists(baseDirAbs))
                {
                    string[] dirs = Directory.GetDirectories(baseDirAbs);
                    Array.Sort(dirs, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < dirs.Length; i++)
                        list.Add(new Entry(Path.GetFileName(dirs[i]), true));

                    string[] files = Directory.GetFiles(baseDirAbs);
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < files.Length; i++)
                        list.Add(new Entry(Path.GetFileName(files[i]), false));
                }
            }
            catch { list.Clear(); }

            _cacheKey = key;
            _cacheEntries = list;
            return list;
        }

        // ---- shared popup plumbing ----

        private void Populate()
        {
            if (_items.Count == 0) { Hide(); return; }

            _list.BeginUpdate();
            try
            {
                _list.Items.Clear();
                for (int i = 0; i < _items.Count; i++)
                    _list.Items.Add(_items[i]);
                _list.SelectedIndex = 0;
            }
            finally { _list.EndUpdate(); }

            SizeAndShow();
        }

        private void SizeAndShow()
        {
            // Match the user's input font (set in Settings) so the list scales on high-DPI screens
            // instead of rendering at the ListBox default size. ItemHeight follows the font.
            if (_txt.Font != null && !_list.Font.Equals(_txt.Font))
                _list.Font = _txt.Font;

            int rows = Math.Min(_items.Count, MaxRows);
            int itemH = _list.ItemHeight > 0 ? _list.ItemHeight : 15;
            int height = rows * itemH + 2;
            int width = _txt.Width;
            if (width < 160) width = 160;

            // Position above the input (it sits at the bottom of the window); drop below if there is no
            // room above. Coordinates are screen-space because the popup is a top-level form.
            Point screen = _txt.PointToScreen(Point.Empty);
            int top = screen.Y - height;
            if (top < 0) top = screen.Y + _txt.Height;
            _popup.Bounds = new Rectangle(screen.X, top, width, height);

            if (!_popup.Visible)
                _popup.ShowNoActivate();
        }

        private void Txt_PreviewKeyDown(object sender, PreviewKeyDownEventArgs e)
        {
            if (!IsOpen) return;
            switch (e.KeyCode)
            {
                case Keys.Tab:
                case Keys.Up:
                case Keys.Down:
                case Keys.Enter:
                case Keys.Escape:
                    e.IsInputKey = true; // ensure KeyDown fires for these instead of being pre-handled
                    break;
            }
        }

        private void Move(int delta)
        {
            if (_list.Items.Count == 0) return;
            int idx = _list.SelectedIndex + delta;
            if (idx < 0) idx = 0;
            if (idx > _list.Items.Count - 1) idx = _list.Items.Count - 1;
            _list.SelectedIndex = idx;
        }

        private void AcceptSelected()
        {
            Item it = _list.SelectedItem as Item;
            if (it == null) return;

            // For a file (or a command name) accepting closes the popup; for a directory we let the
            // text change re-trigger so the user can keep drilling into children.
            _ignoreNextChange = !it.IsDirectory;
            _input.SetInputText(it.Insert, true);
            if (!it.IsDirectory) Hide();
        }

        private void List_MouseClick(object sender, MouseEventArgs e)
        {
            int idx = _list.IndexFromPoint(e.Location);
            if (idx < 0) return;
            _list.SelectedIndex = idx;
            AcceptSelected();
        }

        public void Hide()
        {
            if (_popup != null && _popup.Visible)
                _popup.Hide();
        }

        // ---- helpers / types ----

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++)
                if (char.IsWhiteSpace(s[i])) return i;
            return -1;
        }

        private static int LastSeparator(string s)
        {
            for (int i = s.Length - 1; i >= 0; i--)
                if (s[i] == '/' || s[i] == '\\') return i;
            return -1;
        }

        private sealed class Item
        {
            public string Display;
            public string Insert;
            public bool IsDirectory;
            public override string ToString() { return Display; }
        }

        private struct Entry
        {
            public readonly string Name;
            public readonly bool IsDir;
            public Entry(string name, bool isDir) { Name = name; IsDir = isDir; }
        }

        // A borderless top-level form that never steals focus: the input box keeps keyboard focus while
        // the popup is open (so typing and the arrow/Tab/Enter routing keep working) and clicking an
        // item does not transfer focus either (MA_NOACTIVATE), so no LostFocus race on selection.
        private sealed class PopupForm : Form
        {
            private const int WS_EX_NOACTIVATE = 0x08000000;
            private const int WS_EX_TOOLWINDOW = 0x00000080;
            private const int WM_MOUSEACTIVATE = 0x0021;
            private const int MA_NOACTIVATE = 0x0003;

            public PopupForm()
            {
                FormBorderStyle = FormBorderStyle.None; // border is drawn by the hosted ListBox
                StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false;
                ControlBox = false;
                MinimizeBox = false;
                MaximizeBox = false;
            }

            // Honored by Form.Show: present the window without activating it.
            protected override bool ShowWithoutActivation { get { return true; } }

            protected override CreateParams CreateParams
            {
                get
                {
                    CreateParams cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                // Don't activate (and don't pull focus off the text box) when an item is clicked.
                if (m.Msg == WM_MOUSEACTIVATE)
                {
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                }
                base.WndProc(ref m);
            }

            public void ShowNoActivate()
            {
                if (!Visible) Show();
            }
        }
    }
}
