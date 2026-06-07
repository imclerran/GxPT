using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace GxPT
{
    // Token-aware autocomplete for the input box. Built on a ToolStripDropDown hosting a ListBox: it is
    // a non-activating popup (the caret stays in the text box), auto-closes on outside click/focus loss,
    // and opens upward (the input sits at the bottom of the window).
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

        private readonly ToolStripDropDown _dropDown;
        private readonly ToolStripControlHost _hostCtl;
        private readonly ListBox _list;

        private readonly List<Item> _items = new List<Item>();
        private bool _ignoreNextChange;
        // True once the user has moved the selection with the arrow keys. Enter accepts only after
        // navigation; otherwise Enter falls through and sends, so "/commit"+Enter still sends in one
        // keystroke. Tab always accepts.
        private bool _navigated;

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
            _list.MouseClick += List_MouseClick;

            _hostCtl = new ToolStripControlHost(_list);
            _hostCtl.AutoSize = false;
            _hostCtl.Margin = Padding.Empty;
            _hostCtl.Padding = Padding.Empty;

            _dropDown = new ToolStripDropDown();
            _dropDown.AutoClose = true;        // closes on outside click / focus change
            _dropDown.DropShadowEnabled = true;
            _dropDown.Padding = Padding.Empty;
            _dropDown.Items.Add(_hostCtl);

            // Only TextChanged drives (re)evaluation. We deliberately do NOT hide on the text box's
            // LostFocus: a ToolStripDropDown is a non-activating tool window, but wiring LostFocus risks
            // closing the popup the instant it shows. AutoClose covers clicks elsewhere.
            if (_txt != null)
                _txt.TextChanged += delegate { OnTextChanged(); };
        }

        public bool IsOpen
        {
            get { return _dropDown != null && _dropDown.Visible; }
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
                    AcceptSelected();
                    e.Handled = true; e.SuppressKeyPress = true;
                    return true;
                case Keys.Enter:
                    // Accept only if the user navigated the list; otherwise let Enter send.
                    if (!_navigated) return false;
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

            _navigated = false; // fresh result set: Enter sends until the user navigates again
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
            int rows = Math.Min(_items.Count, MaxRows);
            int itemH = _list.ItemHeight > 0 ? _list.ItemHeight : 15;
            int height = rows * itemH + 4;
            int width = _txt.Width;
            if (width < 120) width = 120;

            Size sz = new Size(width, height);
            _list.Size = sz;
            _hostCtl.Size = sz;

            if (_dropDown.Visible)
            {
                // Reflow so the popup tracks the new row count while it stays open.
                _dropDown.PerformLayout();
            }
            else
            {
                // Open above the input, left-aligned with it.
                _dropDown.Show(_txt, new Point(0, 0), ToolStripDropDownDirection.AboveRight);
            }
        }

        private void Move(int delta)
        {
            if (_list.Items.Count == 0) return;
            _navigated = true;
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
            if (_dropDown != null && _dropDown.Visible)
                _dropDown.Close();
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
    }
}
